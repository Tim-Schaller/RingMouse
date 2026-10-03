using System.Text.Json;
using RingMouse.HidPlusPlus.Transport;

namespace RingMouse.HidPlusPlus.Tests;

/// <summary>Ein aufgezeichneter Frame aus ringmouse-probe --record.</summary>
public sealed record RecordedFrame(double Time, bool IsTx, byte[] Data)
{
    public string Hex => HidppMessage.ToHex(Data);
}

public static class Recording
{
    public static IReadOnlyList<RecordedFrame> Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        var frames = new List<RecordedFrame>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("meta", out _)) continue;
            frames.Add(new RecordedFrame(
                root.GetProperty("t").GetDouble(),
                root.GetProperty("dir").GetString() == "tx",
                HidppMessage.ParseHex(root.GetProperty("data").GetString()!)));
        }
        return frames;
    }

    public static bool Exists(string fileName) =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    /// <summary>Alle empfangenen Reports, die als HID++ parsebar sind.</summary>
    public static IEnumerable<HidppMessage> ReceivedMessages(IEnumerable<RecordedFrame> frames) =>
        frames.Where(f => !f.IsTx).Select(f => HidppMessage.TryParse(f.Data, out var m) ? m : null).OfType<HidppMessage>();

    /// <summary>Antwort auf die erste aufgezeichnete Anfrage mit gleichem Kopf (Index/Feature/Funktion/SW-ID) und Payload-Präfix.</summary>
    public static HidppMessage ResponseTo(IReadOnlyList<RecordedFrame> frames, string requestHexPrefix)
    {
        var prefix = HidppMessage.ParseHex(requestHexPrefix);
        for (var i = 0; i < frames.Count; i++)
        {
            if (!frames[i].IsTx || !frames[i].Data.AsSpan().StartsWith(prefix)) continue;
            var head = frames[i].Data.AsSpan(1, 3).ToArray();
            for (var j = i + 1; j < frames.Count; j++)
                if (!frames[j].IsTx && frames[j].Data.AsSpan(1, 3).SequenceEqual(head))
                    return HidppMessage.TryParse(frames[j].Data, out var m) ? m : throw new InvalidOperationException();
        }
        throw new InvalidOperationException($"Keine Antwort auf {requestHexPrefix} in der Aufzeichnung.");
    }
}

/// <summary>Fake-Port: beantwortet Writes über einen Responder und liefert die Antworten asynchron aus.</summary>
public sealed class FakeHidPort : IHidPort
{
    private readonly Func<byte[], IEnumerable<byte[]>> _responder;

    public FakeHidPort(Func<byte[], IEnumerable<byte[]>> responder, int inputLength = 20, int outputLength = 20)
    {
        _responder = responder;
        Info = new HidDeviceInfo
        {
            Path = @"\\?\fake#col02",
            InstanceId = @"HID\FAKE&COL02\0",
            UsagePage = 0xFF43,
            Usage = 0x0202,
            InputReportLength = inputLength,
            OutputReportLength = outputLength,
            InputReportIds = [inputLength == 7 ? (byte)0x10 : (byte)0x11],
            OutputReportIds = [inputLength == 7 ? (byte)0x10 : (byte)0x11],
        };
    }

    public HidDeviceInfo Info { get; }
    public List<byte[]> Written { get; } = [];
    public bool IsOpen { get; private set; } = true;

    public event HidReportHandler? ReportReceived;
    public event Action<IHidPort, Exception?>? Closed;

    public void Start() { }

    public void Write(ReadOnlySpan<byte> report, TimeSpan timeout)
    {
        if (!IsOpen) throw new ObjectDisposedException(nameof(FakeHidPort));
        var padded = new byte[Info.OutputReportLength];
        report.CopyTo(padded);
        lock (Written) Written.Add(padded);
        var responses = _responder(padded).ToList();
        if (responses.Count == 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2).ConfigureAwait(false);
            foreach (var r in responses) Inject(r);
        });
    }

    public void Inject(byte[] report) => ReportReceived?.Invoke(this, report, System.Diagnostics.Stopwatch.GetTimestamp());

    public void Fail(Exception error)
    {
        IsOpen = false;
        Closed?.Invoke(this, error);
    }

    public void Dispose()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Closed?.Invoke(this, null);
    }
}

/// <summary>Spielt eine Aufzeichnung ab: jede Anfrage wird mit der damals erhaltenen Antwort beantwortet.</summary>
public sealed class ReplayResponder
{
    private readonly Dictionary<string, Queue<byte[]>> _byRequest = new();
    private readonly Dictionary<string, Queue<byte[]>> _byHead = new();

    public ReplayResponder(IReadOnlyList<RecordedFrame> frames)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            if (!frames[i].IsTx) continue;
            var tx = frames[i].Data;
            var head = tx.AsSpan(1, 3).ToArray();
            byte[]? response = null;
            for (var j = i + 1; j < frames.Count && response is null; j++)
            {
                if (frames[j].IsTx) continue;
                var rx = frames[j].Data;
                var isResponse = rx.AsSpan(1, 3).SequenceEqual(head);
                var isError = rx.Length > 5 && (rx[2] == 0xFF || rx[2] == 0x8F) && rx[3] == tx[2] && rx[4] == tx[3];
                if (isResponse || isError) response = rx;
            }
            if (response is null) continue;
            Add(_byRequest, HidppMessage.ToHex(tx), response);
            Add(_byHead, HidppMessage.ToHex(head), response);
        }
    }

    public IEnumerable<byte[]> Respond(byte[] request)
    {
        if (Take(_byRequest, HidppMessage.ToHex(request)) is { } exact) return [exact];
        // Ping enthält ein zufälliges Byte – dann genügt der Kopf.
        if (request[2] == 0x00 && (request[3] >> 4) == 1 && Take(_byHead, HidppMessage.ToHex(request.AsSpan(1, 3))) is { } ping)
        {
            var copy = (byte[])ping.Clone();
            copy[6] = request[6];
            return [copy];
        }
        return [];
    }

    private static void Add(Dictionary<string, Queue<byte[]>> map, string key, byte[] value)
    {
        if (!map.TryGetValue(key, out var q)) map[key] = q = new Queue<byte[]>();
        q.Enqueue(value);
    }

    private static byte[]? Take(Dictionary<string, Queue<byte[]>> map, string key)
    {
        if (!map.TryGetValue(key, out var q) || q.Count == 0) return null;
        var v = q.Dequeue();
        q.Enqueue(v); // wiederholbar
        return v;
    }
}
