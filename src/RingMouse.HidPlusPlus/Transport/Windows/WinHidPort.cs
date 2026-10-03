using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RingMouse.HidPlusPlus.Transport.Windows;

/// <summary>
/// Geöffnete HID-Collection mit eigenem Lese-Thread (Overlapped-I/O) und Schreiben mit Timeout.
/// Puffer und OVERLAPPED-Strukturen liegen in nativem Speicher, damit der Kernel stabil hineinschreiben kann.
/// </summary>
internal sealed unsafe class WinHidPort : IHidPort
{
    /// <summary>So lange höchstens auf den Abschluss einer abgebrochenen I/O warten.</summary>
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(5);

    private readonly SafeFileHandle _handle;
    private readonly Thread _thread;
    private readonly ManualResetEvent _readDone = new(false);
    private readonly ManualResetEvent _writeDone = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _writeLock = new();

    private readonly int _inLen;
    private readonly int _outLen;
    private NativeOverlapped* _readOv;
    private NativeOverlapped* _writeOv;
    private byte* _readBuf;
    private byte* _writeBuf;

    private int _started;
    private int _disposed;
    private int _closedRaised;
    private int _readFreed;
    private bool _writeStuck; // unter _writeLock: abgebrochenes WriteFile nie abgeschlossen → Puffer nicht mehr anfassen
    private volatile bool _stopping;

    public WinHidPort(HidDeviceInfo info, SafeFileHandle handle)
    {
        Info = info;
        _handle = handle;
        _inLen = Math.Max(info.InputReportLength, 1);
        _outLen = info.OutputReportLength;

        _readOv = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _writeOv = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _readBuf = (byte*)NativeMemory.AllocZeroed((nuint)_inLen);
        _writeBuf = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(_outLen, 1));
        _readOv->EventHandle = _readDone.SafeWaitHandle.DangerousGetHandle();
        _writeOv->EventHandle = _writeDone.SafeWaitHandle.DangerousGetHandle();

        // Mehr Puffer im HID-Klassentreiber, damit bei kurzer Blockade keine Events verloren gehen.
        Native.HidD_SetNumInputBuffers(handle, 128);

        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"HID-Reader {info.ProductId:X4} {info.CollectionTag}",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public HidDeviceInfo Info { get; }
    public event HidReportHandler? ReportReceived;
    public event Action<IHidPort, Exception?>? Closed;

    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _closedRaised) == 0;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _thread.Start();
    }

    private void ReadLoop()
    {
        Exception? error = null;
        var pending = false;
        var waits = new WaitHandle[] { _readDone, _stop };
        try
        {
            while (!_stopping)
            {
                _readDone.Reset();
                Native.ResetOverlapped(_readOv);
                if (!Native.ReadFile(_handle, _readBuf, _inLen, IntPtr.Zero, _readOv))
                {
                    var err = Marshal.GetLastPInvokeError();
                    if (err != Native.ERROR_IO_PENDING)
                    {
                        if (!_stopping) error = new HidIoException(err, "ReadFile");
                        break;
                    }
                }
                pending = true;

                if (WaitHandle.WaitAny(waits) == 1) break; // Dispose – Abbruch im finally
                pending = false;

                if (!Native.GetOverlappedResult(_handle, _readOv, out var n, false))
                {
                    var err = Marshal.GetLastPInvokeError();
                    if (!_stopping) error = new HidIoException(err, "ReadFile");
                    break;
                }

                if (n <= 0) continue;
                var report = new byte[n];
                new ReadOnlySpan<byte>(_readBuf, n).CopyTo(report);
                var timestamp = Stopwatch.GetTimestamp();

                var handler = ReportReceived;
                if (handler is null) continue;
                try
                {
                    handler(this, report, timestamp);
                }
                catch
                {
                    // Ein fehlerhafter Handler darf den Lese-Thread nicht beenden.
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            // Puffer, OVERLAPPED und Event erst freigeben, wenn kein ReadFile mehr läuft – sonst schreibt der
            // Kernel in freigegebenen Speicher. Schließt der Treiber die abgebrochene Anfrage nie ab: lieber verwaisen lassen.
            if (!pending || CancelAndWait(_readOv, _readDone)) FreeReadResources();
            RaiseClosed(error);
        }
    }

    /// <summary>Bricht eine laufende I/O ab und wartet begrenzt auf ihren Abschluss. false = läuft womöglich noch.</summary>
    private bool CancelAndWait(NativeOverlapped* overlapped, ManualResetEvent done)
    {
        try
        {
            Native.CancelIoEx(_handle, overlapped);
        }
        catch (ObjectDisposedException)
        {
            // Handle bereits geschlossen – das bricht die I/O ebenfalls ab
        }
        return done.WaitOne(CancelTimeout);
    }

    public void Write(ReadOnlySpan<byte> report, TimeSpan timeout)
    {
        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_writeStuck) throw new IOException("Ein früheres WriteFile hängt im Treiber – Collection wird geschlossen.");
            if (_outLen <= 0) throw new InvalidOperationException("Die Collection hat keinen Output-Report.");
            if (report.Length > _outLen)
                throw new ArgumentException($"Report ({report.Length} Byte) länger als Output-Report ({_outLen} Byte).", nameof(report));

            var span = new Span<byte>(_writeBuf, _outLen);
            span.Clear();
            report.CopyTo(span);

            _writeDone.Reset();
            Native.ResetOverlapped(_writeOv);
            if (!Native.WriteFile(_handle, _writeBuf, _outLen, IntPtr.Zero, _writeOv))
            {
                var err = Marshal.GetLastPInvokeError();
                if (err != Native.ERROR_IO_PENDING) throw new HidIoException(err, "WriteFile");
            }

            if (!_writeDone.WaitOne(timeout))
            {
                if (!CancelAndWait(_writeOv, _writeDone))
                {
                    // Abgebrochene Anfrage kommt nicht zurück: Puffer verwaisen lassen und die Collection schließen,
                    // damit der DeviceService sie neu öffnet.
                    _writeStuck = true;
                    ThreadPool.QueueUserWorkItem(static port => ((WinHidPort)port!).Dispose(), this);
                }
                throw new TimeoutException($"WriteFile: keine Bestätigung nach {timeout.TotalMilliseconds:0} ms (Gerät schläft oder ist getrennt?)");
            }

            if (!Native.GetOverlappedResult(_handle, _writeOv, out _, false))
                throw new HidIoException(Marshal.GetLastPInvokeError(), "WriteFile");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        _stop.Set();

        // Auf dem Lese-Thread selbst (Closed-Handler) ist der Lesezyklus bereits beendet.
        var started = Volatile.Read(ref _started) == 1;
        var readerDone = !started || Thread.CurrentThread == _thread || _thread.Join(TimeSpan.FromSeconds(3));

        lock (_writeLock)
        {
            _handle.Dispose(); // bricht ggf. noch hängende I/O ab
            if (!_writeStuck)
            {
                if (_writeOv != null) { NativeMemory.Free(_writeOv); _writeOv = null; }
                if (_writeBuf != null) { NativeMemory.Free(_writeBuf); _writeBuf = null; }
                _writeDone.Dispose();
            }
        }

        if (!started) FreeReadResources();
        if (readerDone) _stop.Dispose();
        RaiseClosed(_writeStuck ? new HidIoException(Native.ERROR_OPERATION_ABORTED, "WriteFile (hängt)") : null);
    }

    private void FreeReadResources()
    {
        if (Interlocked.Exchange(ref _readFreed, 1) != 0) return;
        if (_readOv != null) { NativeMemory.Free(_readOv); _readOv = null; }
        if (_readBuf != null) { NativeMemory.Free(_readBuf); _readBuf = null; }
        _readDone.Dispose();
    }

    private void RaiseClosed(Exception? error)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) != 0) return;
        try
        {
            Closed?.Invoke(this, error);
        }
        catch
        {
            // ignorieren
        }
    }
}
