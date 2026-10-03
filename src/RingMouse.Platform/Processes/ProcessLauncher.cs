using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using RingMouse.Platform.Windows;

namespace RingMouse.Platform.Processes;

/// <summary>
/// Startet Programme, Dateien, URLs und URIs. Läuft RingMouse selbst mit Adminrechten, wird über die
/// Explorer-Shell (IShellDispatch2.ShellExecute) ohne Adminrechte gestartet – sonst erbt z.B. der Browser
/// die erhöhten Rechte. Muss auf einem STA-Thread laufen (COM).
/// </summary>
public sealed class ProcessLauncher(ILogger? logger = null)
{
    public void Launch(string target, string? arguments = null, string? workingDirectory = null, bool elevated = false, bool hidden = false)
    {
        var file = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        var args = string.IsNullOrWhiteSpace(arguments) ? null : Environment.ExpandEnvironmentVariables(arguments);
        var dir = string.IsNullOrWhiteSpace(workingDirectory) ? null : Environment.ExpandEnvironmentVariables(workingDirectory);

        // shell:-Pfade (z.B. shell:AppsFolder\<AUMID>) versteht zuverlässig nur explorer.exe
        if (file.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            args = file;
            file = "explorer.exe";
        }

        if (elevated)
        {
            StartShell(file, args, dir, "runas", hidden);
            return;
        }

        if (ProcessRights.IsElevated)
        {
            if (ShellExecuteUnelevated(file, args, dir, hidden)) return;
            logger?.LogWarning("Unelevated launch via the Explorer shell failed – starting directly (inherits admin rights): {File}", file);
        }
        StartShell(file, args, dir, null, hidden);
    }

    /// <summary>Startet einen Prozess direkt (ohne Shell), z.B. versteckte PowerShell. Liefert den Prozess.</summary>
    public Process? StartHidden(string file, string arguments, string? workingDirectory = null)
    {
        if (ProcessRights.IsElevated)
        {
            // direkter Start würde Adminrechte vererben → über die Shell, versteckt
            return ShellExecuteUnelevated(file, arguments, workingDirectory, hidden: true) ? null : StartDirect();
        }
        return StartDirect();

        Process? StartDirect() => Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        });
    }

    private static void StartShell(string file, string? args, string? dir, string? verb, bool hidden)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            Arguments = args ?? "",
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
        };
        if (dir is not null) psi.WorkingDirectory = dir;
        if (verb is not null) psi.Verb = verb;
        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC-Abfrage abgebrochen
        }
    }

    /// <summary>Raymond Chens Verfahren: ShellExecute im Kontext des Explorer-Desktops.</summary>
    public bool ShellExecuteUnelevated(string file, string? args, string? dir, bool hidden)
    {
        var com = new List<object>(6);
        object? Track(object? value)
        {
            if (value is not null && Marshal.IsComObject(value) && !com.Exists(o => ReferenceEquals(o, value))) com.Add(value);
            return value;
        }

        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            if (type is null) return false;
            if (Track(Activator.CreateInstance(type)) is not IShellWindows windows) return false;

            object location = 0x0000; // CSIDL_DESKTOP
            object empty = null!; // VT_EMPTY
            var desktop = Track(windows.FindWindowSW(ref location, ref empty, 0x0008 /* SWC_DESKTOP */, out _, 0x0001 /* SWFO_NEEDDISPATCH */));
            if (desktop is not IServiceProvider provider) return false;

            var sidTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var iidShellBrowser = typeof(IShellBrowser).GUID;
            if (provider.QueryService(ref sidTopLevelBrowser, ref iidShellBrowser, out var browserObj) != 0 ||
                Track(browserObj) is not IShellBrowser browser)
                return false;

            var view = (IShellView)Track(browser.QueryActiveShellView())!;
            var iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
            var folderView = Track(view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iidDispatch))!;
            var shell = Track((object)((dynamic)folderView).Application)!;
            ((dynamic)shell).ShellExecute(file, args ?? "", dir ?? "", "open", hidden ? 0 : 1);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "ShellExecute via Explorer failed");
            return false;
        }
        finally
        {
            // RCWs sofort freigeben – sie halten sonst Referenzen in den Explorer-Prozess bis zur Finalisierung.
            for (var i = com.Count - 1; i >= 0; i--)
            {
                try
                {
                    Marshal.FinalReleaseComObject(com[i]);
                }
                catch
                {
                    // egal
                }
            }
        }
    }

    /// <summary>Baut eine PowerShell-Kommandozeile; Befehle werden per -EncodedCommand übergeben (keine Quoting-Probleme).</summary>
    public static (string File, string Arguments) BuildPowerShell(string? script, string? command, string? arguments, bool hidden, bool usePwsh)
    {
        var exe = usePwsh
            ? "pwsh.exe"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var sb = new StringBuilder("-NoProfile -NonInteractive -ExecutionPolicy Bypass");
        if (hidden) sb.Append(" -WindowStyle Hidden");
        if (!string.IsNullOrWhiteSpace(script))
        {
            sb.Append(" -File \"").Append(Environment.ExpandEnvironmentVariables(script.Trim().Trim('"'))).Append('"');
            if (!string.IsNullOrWhiteSpace(arguments)) sb.Append(' ').Append(arguments);
        }
        else
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command ?? ""));
            sb.Append(" -EncodedCommand ").Append(encoded);
        }
        return (exe, sb.ToString());
    }
}

// ---------------------------------------------------------------------- COM-Schnittstellen der Shell

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    int Count { get; }

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object Item([In, Optional] object index);

    [return: MarshalAs(UnmanagedType.IUnknown)]
    object _NewEnum();

    void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);

    void RegisterPending(int lThreadId, [In] ref object pvarloc, [In] ref object pvarlocRoot, int swClass, out int plCookie);

    void Revoke(int lCookie);

    void OnNavigate(int lCookie, [In] ref object pvarLoc);

    void OnActivated(int lCookie, [MarshalAs(UnmanagedType.VariantBool)] bool fActive);

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object FindWindowSW([In] ref object pvarLoc, [In] ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions);

    void OnCreated(int lCookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);

    void ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool fAttach);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    void GetWindow(out IntPtr phwnd);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
    void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
    void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
    void RemoveMenusSB(IntPtr hmenuShared);
    void SetStatusTextSB(IntPtr pszStatusText);
    void EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool fEnable);
    void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
    void BrowseObject(IntPtr pidl, uint wFlags);
    void GetViewStateStream(uint grfMode, IntPtr ppStrm);
    void GetControlWindow(uint id, out IntPtr phwnd);
    void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr pret);

    [return: MarshalAs(UnmanagedType.Interface)]
    IShellView QueryActiveShellView();

    void OnViewWindowActive([MarshalAs(UnmanagedType.Interface)] IShellView pshv);
    void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
}

[ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    void GetWindow(out IntPtr phwnd);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);

    [PreserveSig]
    int TranslateAccelerator(IntPtr pmsg);

    void EnableModeless([MarshalAs(UnmanagedType.Bool)] bool fEnable);
    void UIActivate(uint uState);
    void Refresh();
    void CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
    void DestroyViewWindow();
    void GetCurrentInfo(IntPtr pfs);
    void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
    void SaveViewState();
    void SelectItem(IntPtr pidlItem, uint uFlags);

    [return: MarshalAs(UnmanagedType.Interface)]
    object GetItemObject(uint uItem, ref Guid riid);
}
