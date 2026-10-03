using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RingMouse.HidPlusPlus.Transport.Windows;

/// <summary>
/// Meldet HID-Interface-Arrival/-Removal über CM_Register_Notification (kein Fenster nötig).
/// Callbacks laufen auf einem System-Threadpool-Thread – Handler müssen schnell zurückkehren.
/// </summary>
public sealed unsafe class WinHidDeviceWatcher : IHidDeviceWatcher
{
    private IntPtr _notification;
    private GCHandle _self;

    public event Action<string>? InterfaceArrived;
    public event Action<string>? InterfaceRemoved;

    public void Start()
    {
        if (_notification != IntPtr.Zero) return;
        _self = GCHandle.Alloc(this);

        var filter = new Native.CM_NOTIFY_FILTER
        {
            cbSize = (uint)sizeof(Native.CM_NOTIFY_FILTER),
            FilterType = Native.CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
            ClassGuid = WinHidTransport.HidInterfaceGuid,
        };

        var cr = Native.CM_Register_Notification(&filter, GCHandle.ToIntPtr(_self), &OnNotification, out _notification);
        if (cr != Native.CR_SUCCESS)
        {
            _self.Free();
            _notification = IntPtr.Zero;
            throw new InvalidOperationException($"CM_Register_Notification fehlgeschlagen (CONFIGRET 0x{cr:X}).");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnNotification(IntPtr notify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
    {
        try
        {
            if (context == IntPtr.Zero || eventData == IntPtr.Zero) return 0;
            if (GCHandle.FromIntPtr(context).Target is not WinHidDeviceWatcher self) return 0;
            var link = new string((char*)(eventData + Native.CmNotifyEventDataSymbolicLinkOffset));
            switch (action)
            {
                case Native.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL:
                    self.InterfaceArrived?.Invoke(link);
                    break;
                case Native.CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL:
                    self.InterfaceRemoved?.Invoke(link);
                    break;
            }
        }
        catch
        {
            // niemals Exceptions in nativen Code zurückwerfen
        }
        return 0;
    }

    public void Dispose()
    {
        if (_notification != IntPtr.Zero)
        {
            Native.CM_Unregister_Notification(_notification);
            _notification = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
    }
}
