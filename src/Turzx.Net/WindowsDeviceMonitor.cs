using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Turzx;

internal sealed class WindowsDeviceMonitor : IDisposable
{
    // This is the interface GUID exposed by the installed WinUSB driver for the verified device.
    private static Guid ClassGuid = new("dee824ef-729b-4a0e-9c14-b7117d33a817");
    private readonly Channel<uint> _signals = Channel.CreateUnbounded<uint>(new UnboundedChannelOptions { SingleReader = true });
    private Native.NotifyCallback? _callback;
    private IntPtr _registration;
    private bool _disposed;

    internal ChannelReader<uint> Signals => _signals.Reader;

    internal void Start()
    {
        if (_callback is not null) throw new InvalidOperationException("The monitor has already started.");
        _callback = OnNotification;
        var filter = new Native.NotifyFilter { Size = (uint)Marshal.SizeOf<Native.NotifyFilter>(), ClassGuid = ClassGuid };
        try { Check(Native.CM_Register_Notification(ref filter, IntPtr.Zero, _callback, out _registration), "register notification"); }
        catch { _callback = null; throw; }
        Signal(); // Registration must precede the initial enumeration.
    }

    private uint OnNotification(IntPtr _, IntPtr __, uint action, IntPtr data, uint size)
    {
        if (action <= 1 && size >= 26 && Marshal.ReadInt32(data) == 0)
        {
            var path = Marshal.PtrToStringUni(data + 24, checked((int)(size - 24) / 2))!.Split('\0')[0];
            if (IsTarget(path)) Signal(action);
        }
        return 0;
    }

    internal void Signal(uint action = 2) => _signals.Writer.TryWrite(action);

    internal static string[] Enumerate()
    {
        while (true)
        {
            Check(Native.CM_Get_Device_Interface_List_SizeW(out uint length, ref ClassGuid, null, 0), "get interface list size");
            var buffer = new char[length];
            uint result = Native.CM_Get_Device_Interface_ListW(ref ClassGuid, null, buffer, length, 0);
            if (result == 0x1A) continue; // CR_BUFFER_SMALL: the list changed between calls.
            Check(result, "get interface list");
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(IsTarget).ToArray();
        }
    }

    private static bool IsTarget(string path) => path.Contains("vid_1cbe&pid_0092", StringComparison.OrdinalIgnoreCase);

    internal static string DeviceId(string path)
    {
        var segments = path.Split('#');
        if (segments.Length < 4 || !IsTarget(path)) throw new ArgumentException("Not a TURZX 9.2-inch device interface path.", nameof(path));
        return $"USB\\{segments[1]}\\{segments[2]}".ToUpperInvariant();
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new IOException($"CM {operation} failed: CONFIGRET=0x{result:X}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registration != IntPtr.Zero)
        {
            var result = Native.CM_Unregister_Notification(_registration);
            _registration = IntPtr.Zero;
            _signals.Writer.TryComplete(result == 0 ? null : new IOException($"CM unregister failed: CONFIGRET=0x{result:X}"));
        }
        else _signals.Writer.TryComplete();
        GC.KeepAlive(_callback);
    }
}
