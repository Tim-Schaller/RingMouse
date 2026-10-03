namespace RingMouse.Core;

/// <summary>Ablageorte unter %APPDATA%\RingMouse (per Umgebungsvariable RINGMOUSE_HOME überschreibbar, z.B. für Tests).</summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string SchemaFile => Path.Combine(Root, "config.schema.json");
    public static string StateFile => Path.Combine(Root, "state.json");
    public static string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>Existiert, solange RingMouse den Mauszeiger ausgeblendet hat (Wiederherstellung nach Absturz).</summary>
    public static string CursorMarkerFile => Path.Combine(Root, "cursor-hidden.flag");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string ResolveRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("RINGMOUSE_HOME");
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return Path.GetFullPath(overrideRoot);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RingMouse");
    }
}
