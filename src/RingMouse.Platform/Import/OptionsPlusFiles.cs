using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RingMouse.Core.Import;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.Platform.Import;

/// <summary>
/// Liest die lokalen Daten von Logi Options+: das Einstellungsdokument aus settings.db (über das in Windows enthaltene
/// winsqlite3.dll, aus einer Kopie – Options+ wird nicht gestört) und die Actions-Ring-Profile des Logi Plugin Service.
/// </summary>
public static partial class OptionsPlusFiles
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string SettingsDatabase => Path.Combine(LocalAppData, "LogiOptionsPlus", "settings.db");

    public static string RingProfilesRoot =>
        Path.Combine(LocalAppData, "Logi", "LogiPluginService", "Applications", ActionsRingImporter.RingDeviceType);

    /// <summary>Gibt es lokale Options+-Daten (Einstellungen oder Actions-Ring-Profile)?</summary>
    public static bool Exist() => File.Exists(SettingsDatabase) || Directory.Exists(RingProfilesRoot);

    /// <summary>Alles laden, was der Import braucht; fehlende Teile bleiben leer.</summary>
    public static OptionsPlusImporter Load(ILogger? logger = null) => new(ReadSettingsJson(logger), ReadRingProfiles(logger));

    /// <summary>Einstellungsdokument (JSON) aus einer Kopie von settings.db samt WAL-Dateien; null, wenn nicht lesbar.</summary>
    public static string? ReadSettingsJson(ILogger? logger = null)
    {
        if (!File.Exists(SettingsDatabase)) return null;
        var temp = Path.Combine(Path.GetTempPath(), "ringmouse-optionsplus-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var source = SettingsDatabase + suffix;
                if (File.Exists(source)) CopyShared(source, Path.Combine(temp, "settings.db" + suffix));
            }
            var blob = Sqlite.ReadFirstBlob(Path.Combine(temp, "settings.db"), "SELECT file FROM data ORDER BY _id DESC LIMIT 1");
            return blob is null ? null : Encoding.UTF8.GetString(blob);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException
                                       or InvalidOperationException)
        {
            logger?.LogWarning(ex, "Options+ settings could not be read");
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temp-Ordner – räumt Windows später auf
            }
        }
    }

    /// <summary>Actions-Ring-Profile je App (Standardprofil der App, sonst das erste vorhandene).</summary>
    public static IReadOnlyList<OptionsPlusRingSource> ReadRingProfiles(ILogger? logger = null)
    {
        var result = new List<OptionsPlusRingSource>();
        if (!Directory.Exists(RingProfilesRoot)) return result;
        foreach (var appDir in Directory.EnumerateDirectories(RingProfilesRoot))
        {
            try
            {
                var appName = Path.GetFileName(appDir);
                string display = appName;
                string? process = null;
                string? defaultProfile = null;
                var infoPath = Path.Combine(appDir, "ApplicationInfo.json");
                if (File.Exists(infoPath))
                {
                    using var info = JsonDocument.Parse(File.ReadAllText(infoPath));
                    display = Str(info.RootElement, "displayName") ?? appName;
                    process = Str(info.RootElement, "processOrBundleName");
                    defaultProfile = Str(info.RootElement, "defaultProfileName");
                }

                var profilesDir = Path.Combine(appDir, "Profiles");
                if (!Directory.Exists(profilesDir)) continue;
                var preferred = defaultProfile is null ? null : Path.Combine(profilesDir, defaultProfile, "ProfileInfo.json");
                var profileFile = preferred is not null && File.Exists(preferred)
                    ? preferred
                    : Directory.EnumerateFiles(profilesDir, "ProfileInfo.json", SearchOption.AllDirectories).FirstOrDefault();
                if (profileFile is null) continue;
                result.Add(new OptionsPlusRingSource(appName, display, process, File.ReadAllText(profileFile)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                logger?.LogWarning(ex, "Actions Ring profile {Folder} could not be read", appDir);
            }
        }
        return result;
    }

    /// <summary>ProfileInfo.json aus einem in Options+ exportierten Actions-Ring-Preset (.lp5 = ZIP-Paket).</summary>
    public static string ReadPreset(string lp5Path)
    {
        using var zip = ZipFile.OpenRead(lp5Path);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("ProfileInfo.json", StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e => e.Name.Equals("ProfileInfo.json", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(L("The file is not an Actions Ring preset (ProfileInfo.json missing).",
                        "Die Datei ist kein Actions-Ring-Preset (ProfileInfo.json fehlt)."));
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void CopyShared(string source, string target)
    {
        // Options+ hält die Dateien offen – mit voller Freigabe lesen
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(target);
        input.CopyTo(output);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Minimaler Lesezugriff auf SQLite über das mit Windows ausgelieferte winsqlite3.dll.</summary>
    private static partial class Sqlite
    {
        private const string Library = "winsqlite3.dll";
        private const int Ok = 0;
        private const int Row = 100;
        private const int OpenReadWrite = 2; // die Kopie: SQLite darf -shm/-wal benutzen

        public static byte[]? ReadFirstBlob(string databasePath, string sql)
        {
            if (sqlite3_open_v2(databasePath, out var db, OpenReadWrite, IntPtr.Zero) != Ok)
            {
                var message = Error(db);
                sqlite3_close(db);
                throw new InvalidOperationException($"SQLite open failed: {message}");
            }
            try
            {
                if (sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero) != Ok)
                    throw new InvalidOperationException($"SQLite prepare failed: {Error(db)}");
                try
                {
                    if (sqlite3_step(statement) != Row) return null;
                    var length = sqlite3_column_bytes(statement, 0);
                    var pointer = sqlite3_column_blob(statement, 0);
                    if (pointer == IntPtr.Zero || length <= 0) return null;
                    var data = new byte[length];
                    Marshal.Copy(pointer, data, 0, length);
                    return data;
                }
                finally
                {
                    sqlite3_finalize(statement);
                }
            }
            finally
            {
                sqlite3_close(db);
            }
        }

        private static string Error(IntPtr db) => db == IntPtr.Zero ? "?" : Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "?";

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int sqlite3_open_v2(string filename, out IntPtr db, int flags, IntPtr vfs);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int sqlite3_prepare_v2(IntPtr db, string sql, int byteCount, out IntPtr statement, IntPtr tail);

        [LibraryImport(Library)]
        private static partial int sqlite3_step(IntPtr statement);

        [LibraryImport(Library)]
        private static partial IntPtr sqlite3_column_blob(IntPtr statement, int column);

        [LibraryImport(Library)]
        private static partial int sqlite3_column_bytes(IntPtr statement, int column);

        [LibraryImport(Library)]
        private static partial int sqlite3_finalize(IntPtr statement);

        [LibraryImport(Library)]
        private static partial int sqlite3_close(IntPtr db);

        [LibraryImport(Library)]
        private static partial IntPtr sqlite3_errmsg(IntPtr db);
    }
}
