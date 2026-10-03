using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RingMouse.HidPlusPlus.Transport.Windows;

/// <summary>
/// Schlanker HID-Zugriff über hid.dll/cfgmgr32 – ohne Treiber, ohne Adminrechte.
/// Jede Top-Level-Collection ist unter Windows ein eigenes Interface und wird einzeln geöffnet.
/// </summary>
public sealed unsafe class WinHidTransport : IHidTransport
{
    private static readonly Lazy<Guid> s_hidGuid = new(() =>
    {
        Native.HidD_GetHidGuid(out var g);
        return g;
    });

    /// <summary>GUID_DEVINTERFACE_HID.</summary>
    public static Guid HidInterfaceGuid => s_hidGuid.Value;

    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort? vendorId = null)
    {
        var result = new List<HidDeviceInfo>();
        foreach (var path in GetInterfacePaths())
        {
            // Vorfilter über den Pfad, damit fremde Geräte gar nicht erst geöffnet werden.
            if (vendorId is { } vid && !PathMatchesVendor(path, vid)) continue;
            var info = TryQuery(path);
            if (info is null) continue;
            if (vendorId is { } v && info.VendorId != v) continue;
            result.Add(info);
        }
        return result;
    }

    public IHidPort Open(HidDeviceInfo device)
    {
        var handle = Native.CreateFile(device.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
            Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new HidIoException(err, $"Opening {device.CollectionTag} ({device.VendorId:X4}:{device.ProductId:X4})");
        }
        return new WinHidPort(device, handle);
    }

    /// <summary>Alle aktuell vorhandenen HID-Interface-Pfade.</summary>
    public static IReadOnlyList<string> GetInterfacePaths()
    {
        var guid = HidInterfaceGuid;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (Native.CM_Get_Device_Interface_List_Size(out var length, ref guid, null,
                    Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != Native.CR_SUCCESS || length == 0)
                return [];

            var buffer = new char[length];
            uint cr;
            fixed (char* p = buffer)
            {
                cr = Native.CM_Get_Device_Interface_List(ref guid, null, p, length, Native.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            }
            if (cr == Native.CR_BUFFER_SMALL) continue; // Liste hat sich zwischenzeitlich geändert
            if (cr != Native.CR_SUCCESS) return [];

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return [];
    }

    /// <summary>Liest Attribute, Caps und Strings einer Collection (Handle ohne Lese-/Schreibrecht).</summary>
    public static HidDeviceInfo? TryQuery(string path)
    {
        using var handle = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var attrs = new Native.HIDD_ATTRIBUTES { Size = sizeof(Native.HIDD_ATTRIBUTES) };
        if (!Native.HidD_GetAttributes(handle, ref attrs)) return null;

        ushort usagePage = 0, usage = 0;
        int inLen = 0, outLen = 0, featLen = 0;
        IReadOnlyList<byte> inIds = [], outIds = [], featIds = [];

        if (Native.HidD_GetPreparsedData(handle, out var pp))
        {
            try
            {
                if (Native.HidP_GetCaps(pp, out var caps) == Native.HIDP_STATUS_SUCCESS)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inLen = caps.InputReportByteLength;
                    outLen = caps.OutputReportByteLength;
                    featLen = caps.FeatureReportByteLength;
                    inIds = CollectReportIds(pp, Native.HidP_Input, caps.NumberInputButtonCaps, caps.NumberInputValueCaps);
                    outIds = CollectReportIds(pp, Native.HidP_Output, caps.NumberOutputButtonCaps, caps.NumberOutputValueCaps);
                    featIds = CollectReportIds(pp, Native.HidP_Feature, caps.NumberFeatureButtonCaps, caps.NumberFeatureValueCaps);
                }
            }
            finally
            {
                Native.HidD_FreePreparsedData(pp);
            }
        }

        var instanceId = Native.GetInterfaceString(path, Native.DEVPKEY_Device_InstanceId) ?? "";
        var parent = Native.GetDevNodeString(instanceId, Native.DEVPKEY_Device_Parent) ?? "";

        return new HidDeviceInfo
        {
            Path = path,
            InstanceId = instanceId,
            ParentInstanceId = parent,
            Bus = ClassifyBus(instanceId),
            VendorId = attrs.VendorID,
            ProductId = attrs.ProductID,
            VersionNumber = attrs.VersionNumber,
            UsagePage = usagePage,
            Usage = usage,
            InputReportLength = inLen,
            OutputReportLength = outLen,
            FeatureReportLength = featLen,
            InputReportIds = inIds,
            OutputReportIds = outIds,
            FeatureReportIds = featIds,
            Manufacturer = ReadString(handle, 0),
            Product = ReadString(handle, 1),
            SerialNumber = ReadString(handle, 2),
        };
    }

    /// <summary>Anzeigename eines Geräteknotens (z.B. Eltern-/Großelternknoten eines BLE-Geräts).</summary>
    public static string? GetFriendlyName(string instanceId) =>
        Native.GetDevNodeString(instanceId, Native.DEVPKEY_Device_FriendlyName)
        ?? Native.GetDevNodeString(instanceId, Native.DEVPKEY_Device_BusReportedDeviceDesc)
        ?? Native.GetDevNodeString(instanceId, Native.DEVPKEY_Device_DeviceDesc);

    public static string? GetParentInstanceId(string instanceId) =>
        Native.GetDevNodeString(instanceId, Native.DEVPKEY_Device_Parent);

    internal static HidBusType ClassifyBus(string instanceId)
    {
        var id = instanceId.ToUpperInvariant();
        if (id.Contains("{00001812-0000-1000-8000-00805F9B34FB}")) return HidBusType.BluetoothLe;
        if (id.Contains("{00001124-0000-1000-8000-00805F9B34FB}")) return HidBusType.BluetoothClassic;
        if (id.StartsWith(@"HID\VID_", StringComparison.Ordinal)) return HidBusType.Usb;
        return HidBusType.Unknown;
    }

    internal static bool PathMatchesVendor(string path, ushort vendorId)
    {
        var p = path.ToUpperInvariant();
        var hex = vendorId.ToString("X4");
        return p.Contains($"VID_{hex}")          // USB
               || p.Contains($"VID&02{hex}")     // Bluetooth LE (Vendor-ID-Quelle 02 = USB-IF)
               || p.Contains($"VID&0002{hex}")   // Bluetooth Classic
               || p.Contains($"VID&01{hex}");    // Bluetooth SIG-Vendor-IDs
    }

    private static IReadOnlyList<byte> CollectReportIds(IntPtr pp, int reportType, ushort buttonCaps, ushort valueCaps)
    {
        var ids = new SortedSet<byte>();
        Collect(buttonCaps, isButton: true);
        Collect(valueCaps, isButton: false);
        return ids.ToArray();

        void Collect(ushort count, bool isButton)
        {
            if (count == 0) return;
            var buffer = new byte[count * Native.HidpCapsStructSize];
            var len = count;
            int status;
            fixed (byte* p = buffer)
            {
                status = isButton
                    ? Native.HidP_GetButtonCaps(reportType, p, ref len, pp)
                    : Native.HidP_GetValueCaps(reportType, p, ref len, pp);
            }
            if (status != Native.HIDP_STATUS_SUCCESS) return;
            for (var i = 0; i < len; i++)
                ids.Add(buffer[i * Native.HidpCapsStructSize + Native.HidpCapsReportIdOffset]);
        }
    }

    private static string? ReadString(SafeFileHandle handle, int which)
    {
        const int chars = 256;
        var buffer = stackalloc char[chars];
        new Span<char>(buffer, chars).Clear();
        var ok = which switch
        {
            0 => Native.HidD_GetManufacturerString(handle, buffer, chars * 2),
            1 => Native.HidD_GetProductString(handle, buffer, chars * 2),
            _ => Native.HidD_GetSerialNumberString(handle, buffer, chars * 2),
        };
        if (!ok) return null;
        var s = new string(buffer).TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }
}
