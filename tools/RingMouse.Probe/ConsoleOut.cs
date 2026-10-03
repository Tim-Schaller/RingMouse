using System.Text;

namespace RingMouse.Probe;

internal static class ConsoleOut
{
    private static readonly object s_lock = new();

    public static void Heading(string text)
    {
        lock (s_lock)
        {
            Console.WriteLine();
            WriteColored(ConsoleColor.Cyan, text);
            Console.WriteLine(new string('─', Math.Min(Math.Max(text.Length, 20), 100)));
        }
    }

    public static void Info(string text) { lock (s_lock) Console.WriteLine(text); }

    public static void Hint(string text) { lock (s_lock) WriteColored(ConsoleColor.DarkGray, text); }

    public static void Good(string text) { lock (s_lock) WriteColored(ConsoleColor.Green, text); }

    public static void Warn(string text) { lock (s_lock) WriteColored(ConsoleColor.Yellow, "⚠ " + text); }

    public static void Error(string text) { lock (s_lock) WriteColored(ConsoleColor.Red, "✖ " + text); }

    public static void KeyValue(string key, string? value) { lock (s_lock) Console.WriteLine($"  {key,-16} {value ?? "–"}"); }

    public static void Line(ConsoleColor color, string text) { lock (s_lock) WriteColored(color, text); }

    private static void WriteColored(ConsoleColor color, string text)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }
}

/// <summary>Einfache Textausgabe als Tabelle.</summary>
internal sealed class ConsoleTable(params string[] headers)
{
    private readonly List<string[]> _rows = [];

    public void Add(params object?[] cells) => _rows.Add(cells.Select(c => c?.ToString() ?? "").ToArray());

    public void Print(string indent = "  ")
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, _rows.Count == 0 ? 0 : _rows.Max(r => i < r.Length ? r[i].Length : 0))).ToArray();
        var sb = new StringBuilder();
        sb.Append(indent);
        for (var i = 0; i < headers.Length; i++) sb.Append(headers[i].PadRight(widths[i] + 2));
        ConsoleOut.Line(ConsoleColor.Gray, sb.ToString().TrimEnd());
        foreach (var row in _rows)
        {
            sb.Clear().Append(indent);
            for (var i = 0; i < headers.Length; i++) sb.Append((i < row.Length ? row[i] : "").PadRight(widths[i] + 2));
            ConsoleOut.Info(sb.ToString().TrimEnd());
        }
    }
}
