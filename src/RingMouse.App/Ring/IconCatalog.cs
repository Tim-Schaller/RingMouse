using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RingMouse.Core.Config;
using RingMouse.Platform;

namespace RingMouse.App.Ring;

/// <summary>Ein aufgelöstes Icon: Glyph (Segoe Fluent Icons), Bild oder Kurztext.</summary>
internal sealed record IconSpec(char? Glyph, ImageSource? Image, string? Text)
{
    public static IconSpec None { get; } = new(null, null, null);
    public bool IsEmpty => Glyph is null && Image is null && string.IsNullOrEmpty(Text);
}

/// <summary>
/// Symbolnamen → Glyphen aus "Segoe Fluent Icons" (Windows 11, Fallback "Segoe MDL2 Assets").
/// Die Codepoints wurden gegen die installierte Schrift gerendert und geprüft.
/// </summary>
internal static class IconCatalog
{
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static IReadOnlyDictionary<string, char> Glyphs { get; } = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase)
    {
        ["PlayPause"] = '', ["Play"] = '', ["Pause"] = '', ["Stop"] = '', ["Next"] = '',
        ["Previous"] = '', ["Volume"] = '', ["VolumeUp"] = '', ["VolumeDown"] = '', ["Mute"] = '',
        ["Emoji"] = '', ["Music"] = '', ["Album"] = '', ["Lock"] = '', ["Ticket"] = '',
        ["Tag"] = '', ["Screenshot"] = '', ["Camera"] = '', ["Photo"] = '', ["Folder"] = '',
        ["FolderOpen"] = '', ["Explorer"] = '', ["Shuffle"] = '', ["Repeat"] = '', ["RepeatOne"] = '',
        ["Heart"] = '', ["HeartFill"] = '', ["Playlist"] = '', ["List"] = '', ["Clock"] = '',
        ["Recent"] = '', ["Timer"] = '', ["Check"] = '', ["CheckMark"] = '', ["Save"] = '',
        ["Refresh"] = '', ["Sync"] = '', ["Back"] = '', ["Forward"] = '', ["Cancel"] = '',
        ["Settings"] = '', ["Globe"] = '', ["Web"] = '', ["Terminal"] = '', ["PowerShell"] = '',
        ["Keyboard"] = '', ["Mouse"] = '', ["Mail"] = '', ["Copy"] = '', ["Paste"] = '',
        ["Cut"] = '', ["Undo"] = '', ["Redo"] = '', ["Search"] = '', ["Calendar"] = '',
        ["Star"] = '', ["Link"] = '', ["Edit"] = '', ["Delete"] = '', ["Add"] = '',
        ["Phone"] = '', ["People"] = '', ["Microphone"] = '', ["Video"] = '', ["Monitor"] = '',
        ["Desktop"] = '', ["TaskView"] = '', ["Power"] = '', ["Brightness"] = '', ["Home"] = '',
        ["Pin"] = '', ["Flag"] = '', ["Warning"] = '', ["Info"] = '', ["Code"] = '',
        ["Dpi"] = '', ["Speed"] = '', ["Headphones"] = '', ["Chat"] = '', ["Print"] = '',
        ["Share"] = '', ["Download"] = '', ["Upload"] = '', ["Cloud"] = '', ["Bluetooth"] = '',
        ["Battery"] = '', ["Help"] = '', ["Zoom"] = '', ["FullScreen"] = '', ["Clipboard"] = '',
        ["Laptop"] = '', ["Shield"] = '', ["Document"] = '', ["ChevronRight"] = '',
    };

    private static readonly Dictionary<string, ImageSource?> s_imageCache = new(StringComparer.OrdinalIgnoreCase);

    public static void ClearCache()
    {
        lock (s_imageCache) s_imageCache.Clear();
    }

    /// <summary>Icon eines Segments: explizite Angabe oder passender Standard je Aktionstyp.</summary>
    public static IconSpec Resolve(string? icon, ActionDefinition? action)
    {
        if (!string.IsNullOrWhiteSpace(icon))
        {
            var spec = Parse(icon.Trim());
            if (!spec.IsEmpty) return spec;
        }
        return DefaultFor(action);
    }

    public static IconSpec Parse(string icon)
    {
        if (Glyphs.TryGetValue(icon, out var g)) return new IconSpec(g, null, null);

        var colon = icon.IndexOf(':');
        if (colon > 0)
        {
            var kind = icon[..colon].ToLowerInvariant();
            var value = icon[(colon + 1)..].Trim();
            switch (kind)
            {
                case "glyph" when int.TryParse(value.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, null, out var cp):
                    return new IconSpec((char)cp, null, null);
                case "file":
                    return new IconSpec(null, LoadImage(value), null);
                case "exe":
                    return new IconSpec(null, LoadAssociatedIcon(value), null);
                case "text":
                    return new IconSpec(null, null, value.Length > 3 ? value[..3] : value);
            }
        }
        return icon.Length <= 3 ? new IconSpec(null, null, icon) : IconSpec.None;
    }

    private static IconSpec DefaultFor(ActionDefinition? action) => action switch
    {
        KeysAction => G("Keyboard"),
        MediaAction m => G(m.Key switch
        {
            MediaKey.Next => "Next",
            MediaKey.Previous => "Previous",
            MediaKey.Stop => "Stop",
            MediaKey.VolumeUp => "VolumeUp",
            MediaKey.VolumeDown => "VolumeDown",
            MediaKey.Mute => "Mute",
            _ => "PlayPause",
        }),
        LaunchAction l => LaunchIcon(l.Target),
        SnippetAction => G("Paste"),
        PowerShellAction => G("Terminal"),
        ScreenshotAction => G("Screenshot"),
        SubmenuAction => G("Folder"),
        SystemAction s => G(s.Command switch
        {
            SystemCommand.Lock => "Lock",
            SystemCommand.EmojiPanel => "Emoji",
            SystemCommand.ShowDesktop => "Desktop",
            SystemCommand.TaskView => "TaskView",
            SystemCommand.ClipboardHistory => "Clipboard",
            SystemCommand.MonitorOff => "Monitor",
            _ => "Settings",
        }),
        DpiAction => G("Dpi"),
        AppKeysAction => G("Keyboard"),
        MouseAction => G("Mouse"),
        SequenceAction => G("List"),
        _ => IconSpec.None,
    };

    private static IconSpec LaunchIcon(string target)
    {
        var t = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        if (t.Contains("://", StringComparison.Ordinal) || t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return G("Globe");
        if (t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) ||
            (t.Contains(':') && !Path.IsPathRooted(t)))
            return G("Link");
        var image = LoadAssociatedIcon(t);
        return image is null ? G("Link") : new IconSpec(null, image, null);
    }

    private static IconSpec G(string name) => new(Glyphs[name], null, null);

    private static ImageSource? LoadImage(string path)
    {
        lock (s_imageCache)
        {
            if (s_imageCache.TryGetValue("file:" + path, out var cached)) return cached;
            ImageSource? image = null;
            try
            {
                var full = Environment.ExpandEnvironmentVariables(path.Trim('"'));
                if (File.Exists(full))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 64;
                    bmp.UriSource = new Uri(full);
                    bmp.EndInit();
                    bmp.Freeze();
                    image = bmp;
                }
            }
            catch
            {
                image = null;
            }
            s_imageCache["file:" + path] = image;
            return image;
        }
    }

    private static ImageSource? LoadAssociatedIcon(string path)
    {
        lock (s_imageCache)
        {
            if (s_imageCache.TryGetValue("exe:" + path, out var cached)) return cached;
            ImageSource? image = null;
            var hIcon = IntPtr.Zero;
            try
            {
                hIcon = ShellIcons.GetAssociatedIcon(path);
                if (hIcon != IntPtr.Zero)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    image = source;
                }
            }
            catch
            {
                image = null;
            }
            finally
            {
                ShellIcons.Destroy(hIcon);
            }
            s_imageCache["exe:" + path] = image;
            return image;
        }
    }
}
