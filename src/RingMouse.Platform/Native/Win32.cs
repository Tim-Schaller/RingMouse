using System.Runtime.InteropServices;

namespace RingMouse.Platform.Native;

/// <summary>P/Invoke-Signaturen (user32, kernel32, advapi32, shcore, dwmapi).</summary>
internal static unsafe partial class Win32
{
    // ------------------------------------------------------------------ Strukturen

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    // ------------------------------------------------------------------ Konstanten

    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;

    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_XDOWN = 0x0080;
    internal const uint MOUSEEVENTF_XUP = 0x0100;
    internal const uint XBUTTON1 = 0x0001;
    internal const uint XBUTTON2 = 0x0002;

    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;
    internal const uint LLMHF_INJECTED = 0x01;
    internal const uint LLKHF_INJECTED = 0x10;
    internal const uint LLKHF_UP = 0x80;

    internal const uint WM_QUIT = 0x0012;
    internal const uint WM_APP = 0x8000;
    internal const uint WM_SYSCOMMAND = 0x0112;
    internal const uint PM_NOREMOVE = 0x0000;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNORMAL = 1;
    internal const int SW_SHOWMINIMIZED = 2;
    internal const int SW_MINIMIZE = 6;
    internal const int SW_SHOWNA = 8;
    internal const int SW_RESTORE = 9;
    internal const int SW_SHOW = 5;

    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const int MDT_EFFECTIVE_DPI = 0;

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenElevationTypeClass = 18;
    internal const int TokenElevationClass = 20;
    internal const int TokenUIAccessClass = 26;

    internal const int GWL_EXSTYLE = -20;
    internal const long WS_EX_TOPMOST = 0x00000008;
    internal const long WS_EX_TRANSPARENT = 0x00000020;
    internal const long WS_EX_TOOLWINDOW = 0x00000080;
    internal const long WS_EX_LAYERED = 0x00080000;
    internal const long WS_EX_NOACTIVATE = 0x08000000;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_MESSAGE = new(-3);
    internal static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;
    internal const uint SWP_NOOWNERZORDER = 0x0200;

    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const uint CF_UNICODETEXT = 13;

    // ------------------------------------------------------------------ user32

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumChildWindows(IntPtr hWndParent, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    internal static partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    internal static partial int GetClassName(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static partial IntPtr SetWindowsHookEx(int idHook, delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, IntPtr> lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    internal static partial int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial IntPtr DispatchMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "VkKeyScanExW")]
    private static partial short VkKeyScanExNative(ushort ch, IntPtr dwhkl);

    internal static short VkKeyScanEx(char ch, IntPtr dwhkl) => VkKeyScanExNative(ch, dwhkl);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetKeyboardLayout(uint idThread);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyExW")]
    internal static partial uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LockWorkStation();

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll")]
    internal static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    internal static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    // ------------------------------------------------------------------ kernel32

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr GetModuleHandle(string? lpModuleName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    internal static partial nuint GlobalSize(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GlobalFree(IntPtr hMem);

    // ------------------------------------------------------------------ advapi32

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, void* tokenInformation,
        int tokenInformationLength, out int returnLength);

    // ------------------------------------------------------------------ shcore / dwmapi

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetColorizationColor(out uint pcrColorization, [MarshalAs(UnmanagedType.Bool)] out bool pfOpaqueBlend);
}
