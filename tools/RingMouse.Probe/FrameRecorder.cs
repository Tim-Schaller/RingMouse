using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RingMouse.HidPlusPlus;

namespace RingMouse.Probe;

/// <summary>
/// Schreibt jeden Frame als JSON-Zeile: {"t":12.345,"dir":"tx","data":"11 FF 00 1A ..."}.
/// Die erste Zeile enthält Metadaten. Das Format lesen die Unit-Tests als Fixture.
/// </summary>
internal sealed class FrameRecorder : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly object _lock = new();

    public FrameRecorder(string path, IReadOnlyDictionary<string, string> meta)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        _writer.WriteLine(JsonSerializer.Serialize(new { meta }));
    }

    public void Attach(HidppChannel channel) => channel.FrameTraced += OnFrame;

    private void OnFrame(HidppChannel channel, FrameDirection direction, byte[] frame, long timestamp)
    {
        var t = Stopwatch.GetElapsedTime(_start, timestamp).TotalMilliseconds / 1000.0;
        var line = JsonSerializer.Serialize(new
        {
            t = Math.Round(t, 4),
            dir = direction == FrameDirection.Tx ? "tx" : "rx",
            data = HidppMessage.ToHex(frame),
        });
        lock (_lock) _writer.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }
}
