using System.Text;

namespace RingMouse.HidPlusPlus.Features;

public enum DeviceKind : byte
{
    Keyboard = 0,
    RemoteControl = 1,
    Numpad = 2,
    Mouse = 3,
    Trackpad = 4,
    Trackball = 5,
    Presenter = 6,
    Receiver = 7,
    Headset = 8,
    Webcam = 9,
    SteeringWheel = 10,
    Joystick = 11,
    Gamepad = 12,
    Dock = 13,
    Speaker = 14,
    Microphone = 15,
    IlluminationLight = 16,
    ProgrammableController = 17,
    CarSimPedals = 18,
    Adapter = 19,
    Unknown = 0xFF,
}

/// <summary>Eine Firmware-Einheit aus DEVICE_FW_VERSION (0x0003).</summary>
public sealed record FirmwareEntity(int Index, byte Type, string Prefix, byte Number, byte Revision, ushort Build, bool Active, ushort TransportPid)
{
    public string TypeName => Type switch
    {
        0 => "Main application",
        1 => "Bootloader",
        2 => "Hardware",
        3 => "Touchpad",
        4 => "Optical sensor",
        5 => "Softdevice",
        6 => "RF companion MCU",
        7 => "Factory application",
        8 => "RGB custom effect",
        9 => "Motor drive",
        _ => $"Type {Type}",
    };

    /// <summary>Anzeige wie "MPM 19.01.B0021" (Nummer/Revision/Build sind BCD).</summary>
    public string VersionText => Type == 2
        ? $"HW {Number:X2}"
        : $"{Prefix} {Number:X2}.{Revision:X2}.B{Build:X4}".Trim();

    public static FirmwareEntity Parse(int index, ReadOnlySpan<byte> d)
    {
        // Fehlende Bytes (kurze/fehlerhafte Antwort) als 0 behandeln statt zu werfen.
        static byte B(ReadOnlySpan<byte> s, int i) => i < s.Length ? s[i] : (byte)0;
        var prefix = d.Length >= 4 ? Encoding.ASCII.GetString(d.Slice(1, 3)).TrimEnd('\0', ' ') : "";
        return new FirmwareEntity(index, B(d, 0), prefix, B(d, 4), B(d, 5), (ushort)((B(d, 6) << 8) | B(d, 7)),
            (B(d, 8) & 0x01) != 0, (ushort)((B(d, 9) << 8) | B(d, 10)));
    }
}

/// <summary>Kopfdaten aus DEVICE_FW_VERSION.getDeviceInfo.</summary>
public sealed record DeviceFirmwareInfo(int EntityCount, uint UnitId, ushort Transport, IReadOnlyList<ushort> ModelIds, byte ExtendedModelId, byte Capabilities)
{
    public string UnitIdText => UnitId.ToString("X8");
    public bool SerialNumberSupported => (Capabilities & 0x01) != 0;

    public static DeviceFirmwareInfo Parse(ReadOnlySpan<byte> d)
    {
        // Fehlende Bytes (kurze/fehlerhafte Antwort) als 0 behandeln statt zu werfen.
        static byte B(ReadOnlySpan<byte> s, int i) => i < s.Length ? s[i] : (byte)0;
        var unitId = (uint)((B(d, 1) << 24) | (B(d, 2) << 16) | (B(d, 3) << 8) | B(d, 4));
        var transport = (ushort)((B(d, 5) << 8) | B(d, 6));
        var models = new List<ushort>(3);
        for (var i = 0; i < 3; i++)
        {
            var pid = (ushort)((B(d, 7 + i * 2) << 8) | B(d, 8 + i * 2));
            if (pid != 0) models.Add(pid);
        }
        return new DeviceFirmwareInfo(B(d, 0), unitId, transport, models, B(d, 13), B(d, 14));
    }
}

/// <summary>Name, Typ und Firmware (Features 0x0005 und 0x0003).</summary>
public static class DeviceIdentity
{
    public static async Task<string?> GetNameAsync(HidppDevice device, CancellationToken ct = default)
    {
        var feature = await device.GetFeatureAsync(FeatureIds.DeviceName, ct).ConfigureAwait(false);
        if (feature is null) return null;

        var length = (await device.CallAsync(feature.Index, 0x00, null, ct).ConfigureAwait(false))[0];
        var bytes = new List<byte>(length);
        while (bytes.Count < length)
        {
            var chunk = await device.CallAsync(feature.Index, 0x01, [(byte)bytes.Count], ct).ConfigureAwait(false);
            var take = Math.Min(chunk.Length, length - bytes.Count);
            if (take <= 0) break;
            bytes.AddRange(chunk.AsSpan(0, take).ToArray());
        }
        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0').Trim();
    }

    public static async Task<DeviceKind> GetKindAsync(HidppDevice device, CancellationToken ct = default)
    {
        var feature = await device.GetFeatureAsync(FeatureIds.DeviceName, ct).ConfigureAwait(false);
        if (feature is null) return DeviceKind.Unknown;
        var r = await device.CallAsync(feature.Index, 0x02, null, ct).ConfigureAwait(false);
        return Enum.IsDefined(typeof(DeviceKind), r[0]) ? (DeviceKind)r[0] : DeviceKind.Unknown;
    }

    public static async Task<DeviceFirmwareInfo?> GetFirmwareInfoAsync(HidppDevice device, CancellationToken ct = default)
    {
        var feature = await device.GetFeatureAsync(FeatureIds.DeviceFwVersion, ct).ConfigureAwait(false);
        if (feature is null) return null;
        return DeviceFirmwareInfo.Parse(await device.CallAsync(feature.Index, 0x00, null, ct).ConfigureAwait(false));
    }

    public static async Task<IReadOnlyList<FirmwareEntity>> GetFirmwareEntitiesAsync(HidppDevice device, CancellationToken ct = default)
    {
        var feature = await device.GetFeatureAsync(FeatureIds.DeviceFwVersion, ct).ConfigureAwait(false);
        if (feature is null) return [];
        var info = DeviceFirmwareInfo.Parse(await device.CallAsync(feature.Index, 0x00, null, ct).ConfigureAwait(false));
        var list = new List<FirmwareEntity>(info.EntityCount);
        for (var i = 0; i < info.EntityCount; i++)
            list.Add(FirmwareEntity.Parse(i, await device.CallAsync(feature.Index, 0x01, [(byte)i], ct).ConfigureAwait(false)));
        return list;
    }

    public static async Task<string?> GetSerialNumberAsync(HidppDevice device, CancellationToken ct = default)
    {
        var feature = await device.GetFeatureAsync(FeatureIds.DeviceFwVersion, ct).ConfigureAwait(false);
        if (feature is null) return null;
        var info = DeviceFirmwareInfo.Parse(await device.CallAsync(feature.Index, 0x00, null, ct).ConfigureAwait(false));
        if (!info.SerialNumberSupported) return null;
        var r = await device.CallAsync(feature.Index, 0x02, null, ct).ConfigureAwait(false);
        return Encoding.ASCII.GetString(r, 0, Math.Min(12, r.Length)).TrimEnd('\0').Trim();
    }
}
