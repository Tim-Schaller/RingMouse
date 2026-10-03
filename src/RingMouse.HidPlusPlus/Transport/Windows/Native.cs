using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RingMouse.HidPlusPlus.Transport.Windows;

/// <summary>Win32-/HID-/CfgMgr-Signaturen für den HID-Transport.</summary>
internal static unsafe partial class Native
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x1;
    internal const uint FILE_SHARE_WRITE = 0x2;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    internal const int ERROR_IO_PENDING = 997;
    internal const int ERROR_OPERATION_ABORTED = 995;

    internal const int HIDP_STATUS_SUCCESS = 0x00110000;
    internal const int HidP_Input = 0;
    internal const int HidP_Output = 1;
    internal const int HidP_Feature = 2;

    /// <summary>sizeof(HIDP_BUTTON_CAPS) == sizeof(HIDP_VALUE_CAPS) == 72; ReportID liegt bei Offset 2.</summary>
    internal const int HidpCapsStructSize = 72;
    internal const int HidpCapsReportIdOffset = 2;

    internal const uint CR_SUCCESS = 0x00;
    internal const uint CR_BUFFER_SMALL = 0x1A;
    internal const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x0;
    internal const uint CM_LOCATE_DEVNODE_NORMAL = 0x0;

    internal const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    internal const uint CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
    internal const uint CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_CAPS
    {
        public ushort Usage;          // Achtung: Usage steht VOR UsagePage
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct DEVPROPKEY(Guid fmtid, uint pid)
    {
        public readonly Guid Fmtid = fmtid;
        public readonly uint Pid = pid;
    }

    /// <summary>CM_NOTIFY_FILTER (x64: 16 Byte Kopf + 400 Byte Union).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    internal struct CM_NOTIFY_FILTER
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    /// <summary>Offset des SymbolicLink-Strings in CM_NOTIFY_EVENT_DATA (FilterType, Reserved, ClassGuid).</summary>
    internal const int CmNotifyEventDataSymbolicLinkOffset = 24;

    internal static readonly DEVPROPKEY DEVPKEY_Device_InstanceId = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    internal static readonly DEVPROPKEY DEVPKEY_Device_Parent = new(new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), 8);
    internal static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    internal static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    internal static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);

    // ---------------------------------------------------------------- hid.dll

    [LibraryImport("hid.dll")]
    internal static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_FreePreparsedData(IntPtr preparsedData);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetButtonCaps(int reportType, byte* buttonCaps, ref ushort length, IntPtr preparsedData);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetValueCaps(int reportType, byte* valueCaps, ref ushort length, IntPtr preparsedData);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_GetProductString(SafeFileHandle device, char* buffer, uint bufferLength);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_GetManufacturerString(SafeFileHandle device, char* buffer, uint bufferLength);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_GetSerialNumberString(SafeFileHandle device, char* buffer, uint bufferLength);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool HidD_SetNumInputBuffers(SafeFileHandle device, uint numberBuffers);

    // ---------------------------------------------------------------- kernel32

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadFile(SafeFileHandle file, byte* buffer, int numberOfBytesToRead, IntPtr numberOfBytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteFile(SafeFileHandle file, byte* buffer, int numberOfBytesToWrite, IntPtr numberOfBytesWritten, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, out int numberOfBytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);

    // ---------------------------------------------------------------- cfgmgr32

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    internal static partial uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceClassGuid, char* deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    internal static partial uint CM_Get_Device_Interface_List(ref Guid interfaceClassGuid, char* deviceId, char* buffer, uint bufferLength, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CM_Get_Device_Interface_Property(string deviceInterface, in DEVPROPKEY propertyKey, out uint propertyType, byte* buffer, ref uint bufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    internal static partial uint CM_Get_DevNode_Property(uint devInst, in DEVPROPKEY propertyKey, out uint propertyType, byte* buffer, ref uint bufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Register_Notification(CM_NOTIFY_FILTER* filter, IntPtr context,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, uint, uint> callback, out IntPtr notifyContext);

    [LibraryImport("cfgmgr32.dll")]
    internal static partial uint CM_Unregister_Notification(IntPtr notifyContext);

    // ---------------------------------------------------------------- Helfer

    internal static void ResetOverlapped(NativeOverlapped* ov)
    {
        var ev = ov->EventHandle;
        *ov = default;
        ov->EventHandle = ev;
    }

    internal static string? GetInterfaceString(string deviceInterface, in DEVPROPKEY key)
    {
        uint size = 0;
        var cr = CM_Get_Device_Interface_Property(deviceInterface, in key, out _, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return null;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            cr = CM_Get_Device_Interface_Property(deviceInterface, in key, out _, p, ref size, 0);
        }
        return cr == CR_SUCCESS ? DecodeUtf16(buffer, (int)size) : null;
    }

    internal static string? GetDevNodeString(uint devInst, in DEVPROPKEY key)
    {
        uint size = 0;
        var cr = CM_Get_DevNode_Property(devInst, in key, out _, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return null;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            cr = CM_Get_DevNode_Property(devInst, in key, out _, p, ref size, 0);
        }
        return cr == CR_SUCCESS ? DecodeUtf16(buffer, (int)size) : null;
    }

    internal static string? GetDevNodeString(string instanceId, in DEVPROPKEY key)
    {
        if (string.IsNullOrEmpty(instanceId)) return null;
        return CM_Locate_DevNode(out var devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS
            ? GetDevNodeString(devInst, in key)
            : null;
    }

    private static string DecodeUtf16(byte[] buffer, int size)
    {
        var s = System.Text.Encoding.Unicode.GetString(buffer, 0, size);
        var nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }
}
