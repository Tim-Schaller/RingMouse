using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RingMouse.Platform.Native;

namespace RingMouse.Platform.Windows;

public enum ElevationState
{
    Unknown,
    NotElevated,
    Elevated,
    /// <summary>Token nicht lesbar – typischerweise Adminfenster eines anderen Kontos (z.B. admin_*) oder geschützter Prozess.</summary>
    AccessDenied,
}

public sealed record ForegroundWindowInfo(IntPtr Handle, uint ProcessId, string? ProcessName, string? ProcessPath, ElevationState Elevation)
{
    public bool IsLikelyElevated => Elevation is ElevationState.Elevated or ElevationState.AccessDenied;
    public static ForegroundWindowInfo None { get; } = new(IntPtr.Zero, 0, null, null, ElevationState.Unknown);
}

/// <summary>Vordergrundfenster → Prozessname und Elevation.</summary>
public static unsafe class ForegroundWindow
{
    public static ForegroundWindowInfo Capture()
    {
        var hwnd = Win32.GetForegroundWindow();
        return hwnd == IntPtr.Zero ? ForegroundWindowInfo.None : Describe(hwnd);
    }

    public static ForegroundWindowInfo Describe(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        var (path, elevation) = QueryProcess(pid);
        var name = path is null ? null : Path.GetFileName(path);

        // UWP-Apps laufen in ApplicationFrameHost – die eigentliche App ist ein Kindfenster eines anderen Prozesses.
        if (string.Equals(name, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var childPid = FindChildProcess(hwnd, pid);
            if (childPid != 0)
            {
                var (childPath, childElevation) = QueryProcess(childPid);
                if (childPath is not null) return new ForegroundWindowInfo(hwnd, childPid, Path.GetFileName(childPath), childPath, childElevation);
            }
        }
        return new ForegroundWindowInfo(hwnd, pid, name, path, elevation);
    }

    public static (string? Path, ElevationState Elevation) QueryProcess(uint pid)
    {
        if (pid == 0) return (null, ElevationState.Unknown);
        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return (null, Marshal.GetLastPInvokeError() == 5 ? ElevationState.AccessDenied : ElevationState.Unknown);
        try
        {
            string? path = null;
            var buffer = stackalloc char[1024];
            uint size = 1024;
            if (Win32.QueryFullProcessImageName(process, 0, buffer, ref size)) path = new string(buffer, 0, (int)size);

            ElevationState elevation;
            if (!Win32.OpenProcessToken(process, Win32.TOKEN_QUERY, out var token))
            {
                elevation = Marshal.GetLastPInvokeError() == 5 ? ElevationState.AccessDenied : ElevationState.Unknown;
            }
            else
            {
                try
                {
                    Win32.TOKEN_ELEVATION te;
                    elevation = Win32.GetTokenInformation(token, Win32.TokenElevationClass, &te, sizeof(Win32.TOKEN_ELEVATION), out _)
                        ? (te.TokenIsElevated != 0 ? ElevationState.Elevated : ElevationState.NotElevated)
                        : ElevationState.Unknown;
                }
                finally
                {
                    Win32.CloseHandle(token);
                }
            }
            return (path, elevation);
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    private sealed class ChildSearch(uint ownerPid)
    {
        public uint OwnerPid { get; } = ownerPid;
        public uint Found { get; set; }
    }

    private static uint FindChildProcess(IntPtr hwnd, uint ownerPid)
    {
        var state = new ChildSearch(ownerPid);
        var handle = GCHandle.Alloc(state);
        try
        {
            Win32.EnumChildWindows(hwnd, &EnumChild, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return state.Found;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumChild(IntPtr child, IntPtr lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is not ChildSearch s) return 0;
        Win32.GetWindowThreadProcessId(child, out var pid);
        if (pid != 0 && pid != s.OwnerPid)
        {
            s.Found = pid;
            return 0; // stoppen
        }
        return 1;
    }
}

/// <summary>Rechte des eigenen Prozesses.</summary>
public static unsafe class ProcessRights
{
    private const int TokenElevationTypeDefault = 1;
    private const int TokenElevationTypeFull = 2;
    private const int TokenElevationTypeLimited = 3;

    public static bool IsElevated => Environment.IsPrivilegedProcess;

    /// <summary>uiAccess-Token (signierte Exe mit uiAccess="true" aus sicherem Ort) – umgeht UIPI ohne Adminrechte.</summary>
    public static bool HasUiAccess => QueryOwnToken(Win32.TokenUIAccessClass) != 0;

    /// <summary>true, wenn das Konto ein Administrator mit geteiltem Token ist (kann per UAC ohne anderes Konto erhöhen).</summary>
    public static bool UserIsAdministrator
    {
        get
        {
            var type = QueryOwnToken(Win32.TokenElevationTypeClass);
            return type is TokenElevationTypeFull or TokenElevationTypeLimited || IsElevated;
        }
    }

    /// <summary>Kann dieser Prozess Eingaben an Fenster mit höheren Rechten senden?</summary>
    public static bool CanDriveElevatedWindows => IsElevated || HasUiAccess;

    private static int QueryOwnToken(int infoClass)
    {
        if (!Win32.OpenProcessToken(Win32.GetCurrentProcess(), Win32.TOKEN_QUERY, out var token)) return 0;
        try
        {
            int value;
            return Win32.GetTokenInformation(token, infoClass, &value, sizeof(int), out _) ? value : 0;
        }
        finally
        {
            Win32.CloseHandle(token);
        }
    }
}
