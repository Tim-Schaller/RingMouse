using System.Globalization;
using System.Text;

namespace RingMouse.Core.Input;

/// <summary>
/// Ersetzt Platzhalter in Textbausteinen: {now}, {now:dd.MM.yyyy HH:mm}, {date}, {time}, {clipboard},
/// {user}, {computer}, {newline}, {tab}. "{{" und "}}" ergeben geschweifte Klammern; Unbekanntes bleibt stehen.
/// </summary>
public static class SnippetTemplate
{
    public static string Expand(string template, Func<string?>? clipboard = null, DateTime? now = null, CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var time = now ?? DateTime.Now;
        var ci = culture ?? CultureInfo.CurrentCulture;
        var sb = new StringBuilder(template.Length);
        string? clipCache = null;
        var clipLoaded = false;

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                sb.Append('{');
                i++;
                continue;
            }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                sb.Append('}');
                i++;
                continue;
            }
            if (c != '{')
            {
                sb.Append(c);
                continue;
            }

            var end = template.IndexOf('}', i + 1);
            if (end < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            var body = template.Substring(i + 1, end - i - 1);
            var colon = body.IndexOf(':');
            var name = (colon < 0 ? body : body[..colon]).Trim().ToLowerInvariant();
            var format = colon < 0 ? null : body[(colon + 1)..];

            string? value;
            try
            {
                value = name switch
                {
                    "now" or "datetime" => time.ToString(string.IsNullOrEmpty(format) ? "g" : format, ci),
                    "date" or "datum" => time.ToString(string.IsNullOrEmpty(format) ? "d" : format, ci),
                    "time" or "zeit" => time.ToString(string.IsNullOrEmpty(format) ? "t" : format, ci),
                    "clipboard" or "zwischenablage" => LoadClipboard(),
                    "user" or "benutzer" => Environment.UserName,
                    "computer" => Environment.MachineName,
                    "newline" or "nl" => "\n",
                    "tab" => "\t",
                    _ => null,
                };
            }
            catch (FormatException)
            {
                value = null;
            }

            if (value is null) sb.Append(template, i, end - i + 1);
            else sb.Append(value);
            i = end;
        }
        return sb.ToString();

        string LoadClipboard()
        {
            if (!clipLoaded)
            {
                clipCache = clipboard?.Invoke();
                clipLoaded = true;
            }
            return clipCache ?? "";
        }
    }

    /// <summary>true, wenn der Text den Platzhalter {clipboard} enthält (dann muss die Zwischenablage gelesen werden).</summary>
    public static bool UsesClipboard(string template) =>
        template.Contains("{clipboard", StringComparison.OrdinalIgnoreCase) ||
        template.Contains("{zwischenablage", StringComparison.OrdinalIgnoreCase);
}
