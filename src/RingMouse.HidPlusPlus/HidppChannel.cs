using System.Diagnostics;
using RingMouse.HidPlusPlus.Transport;

namespace RingMouse.HidPlusPlus;

public enum FrameDirection
{
    Tx,
    Rx,
}

public delegate void HidppMessageHandler(HidppChannel channel, HidppMessage message, long timestamp);

public delegate void FrameTraceHandler(HidppChannel channel, FrameDirection direction, byte[] frame, long timestamp);

/// <summary>
/// Ein HID++-Kanal zu einem physischen Gerät bzw. Receiver: bündelt die Short- (0x10) und
/// Long-Collection (0x11), wählt beim Senden die passende, ordnet Antworten offenen Anfragen zu
/// und reicht alles andere als Notification weiter.
/// </summary>
/// <remarks>
/// Unter Windows ist jede Top-Level-Collection ein eigenes Handle. Bei Bluetooth LE gibt es nur
/// die Long-Collection; Short-Anfragen werden dann transparent als Long-Report gesendet.
/// </remarks>
public sealed class HidppChannel : IDisposable
{
    private readonly IHidPort? _shortPort;
    private readonly IHidPort? _longPort;
    private readonly object _pendingLock = new();
    private readonly List<PendingRequest> _pending = [];
    private int _closed;

    public HidppChannel(string name, IHidPort? shortPort, IHidPort? longPort)
    {
        if (shortPort is null && longPort is null)
            throw new ArgumentException("Mindestens eine HID++-Collection wird benötigt.");
        Name = name;
        _shortPort = shortPort;
        _longPort = longPort;
        foreach (var port in Ports)
        {
            port.ReportReceived += OnReport;
            port.Closed += OnPortClosed;
        }
    }

    public string Name { get; }
    public bool HasShortPort => _shortPort is not null;
    public bool HasLongPort => _longPort is not null;
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public Exception? CloseReason { get; private set; }

    /// <summary>Maximale Wartezeit auf den Abschluss eines WriteFile.</summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Alle Reports, die keiner offenen Anfrage zugeordnet wurden (Events, fremde Antworten).</summary>
    public event HidppMessageHandler? MessageReceived;

    /// <summary>Jeder gesendete und empfangene Rohreport (für Raw-Log und Aufzeichnung).</summary>
    public event FrameTraceHandler? FrameTraced;

    /// <summary>Kanal geschlossen (Gerät entfernt, I/O-Fehler oder Dispose).</summary>
    public event Action<HidppChannel, Exception?>? Closed;

    private IEnumerable<IHidPort> Ports
    {
        get
        {
            if (_shortPort is not null) yield return _shortPort;
            if (_longPort is not null) yield return _longPort;
        }
    }

    public IEnumerable<HidDeviceInfo> Collections => Ports.Select(p => p.Info);

    public void Start()
    {
        foreach (var port in Ports) port.Start();
    }

    /// <summary>Sendet eine Nachricht ohne auf Antwort zu warten.</summary>
    public void Send(HidppMessage message)
    {
        if (IsClosed) throw new HidppTransportException("Kanal ist geschlossen.", CloseReason);

        IHidPort port;
        HidppMessage wire;
        if (message.IsShort)
        {
            if (_shortPort is not null) (port, wire) = (_shortPort, message);
            else (port, wire) = (_longPort!, message.ToLong());
        }
        else if (message.IsLong)
        {
            if (_longPort is not null) (port, wire) = (_longPort, message);
            else if (message.FitsShort) (port, wire) = (_shortPort!, message.ToShort());
            else throw new HidppTransportException("Gerät bietet keine Long-Report-Collection (0x11).");
        }
        else
        {
            throw new HidppTransportException($"Report-ID 0x{message.ReportId:X2} wird nicht unterstützt.");
        }

        var bytes = wire.ToArray();
        FrameTraced?.Invoke(this, FrameDirection.Tx, bytes, Stopwatch.GetTimestamp());
        try
        {
            port.Write(bytes, WriteTimeout);
        }
        catch (TimeoutException ex)
        {
            throw new HidppTransportException(ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new HidppTransportException($"Schreiben fehlgeschlagen: {ex.Message}", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new HidppTransportException("Kanal ist geschlossen.", ex);
        }
        catch (Exception ex) when (ex is not HidppException)
        {
            // z.B. Collection ohne Output-Report – für Aufrufer wie jeder andere Transportfehler
            throw new HidppTransportException($"Schreiben fehlgeschlagen: {ex.Message}", ex);
        }
    }

    /// <summary>Sendet eine Anfrage und wartet auf die zugehörige Antwort bzw. Fehlermeldung.</summary>
    public async Task<HidppMessage> TransactAsync(HidppMessage request, IResponseMatcher matcher, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var pending = new PendingRequest(matcher, request);
        lock (_pendingLock) _pending.Add(pending);
        try
        {
            Send(request);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using var registration = timeoutCts.Token.Register(
                static state => ((PendingRequest)state!).Completion.TrySetCanceled(), pending);
            try
            {
                return await pending.Completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HidppTimeoutException(request, timeout);
            }
        }
        finally
        {
            lock (_pendingLock) _pending.Remove(pending);
        }
    }

    private void OnReport(IHidPort port, byte[] report, long timestamp)
    {
        FrameTraced?.Invoke(this, FrameDirection.Rx, report, timestamp);
        if (!HidppMessage.TryParse(report, out var message)) return;

        PendingRequest? hit = null;
        var kind = MatchKind.None;
        byte code = 0;
        var isV10 = false;
        lock (_pendingLock)
        {
            foreach (var p in _pending)
            {
                kind = p.Matcher.Match(message, out code, out isV10);
                if (kind == MatchKind.None) continue;
                hit = p;
                break;
            }
            if (hit is not null) _pending.Remove(hit);
        }

        if (hit is not null)
        {
            if (kind == MatchKind.Response) hit.Completion.TrySetResult(message);
            else hit.Completion.TrySetException(new HidppErrorException(isV10, code, hit.Request));
            return;
        }

        MessageReceived?.Invoke(this, message, timestamp);
    }

    private void OnPortClosed(IHidPort port, Exception? error) =>
        Close(error ?? new HidppTransportException($"Collection {port.Info.CollectionTag} wurde geschlossen."));

    private void Close(Exception? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseReason = reason;

        PendingRequest[] pending;
        lock (_pendingLock)
        {
            pending = [.. _pending];
            _pending.Clear();
        }
        foreach (var p in pending)
            p.Completion.TrySetException(new HidppTransportException("Kanal wurde geschlossen.", reason));

        foreach (var port in Ports)
        {
            port.ReportReceived -= OnReport;
            port.Closed -= OnPortClosed;
            try { port.Dispose(); } catch { /* egal */ }
        }

        try { Closed?.Invoke(this, reason is ObjectDisposedException ? null : reason); } catch { /* egal */ }
    }

    public void Dispose() => Close(new ObjectDisposedException(nameof(HidppChannel)));

    private sealed class PendingRequest(IResponseMatcher matcher, HidppMessage request)
    {
        public IResponseMatcher Matcher { get; } = matcher;
        public HidppMessage Request { get; } = request;
        public TaskCompletionSource<HidppMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
