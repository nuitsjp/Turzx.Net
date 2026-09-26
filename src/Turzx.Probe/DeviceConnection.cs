using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class DeviceConnection
{
    // Interface class exposed by the installed WinUSB driver on this device.
    private static Guid interfaceClass = new("dee824ef-729b-4a0e-9c14-b7117d33a817");
    private static bool IsTarget(string path) => path.Contains("vid_1cbe&pid_0092", StringComparison.OrdinalIgnoreCase);

    internal static int Status()
    {
        try
        {
            var paths = Enumerate();
            Log($"STATUS connected={paths.Length > 0} count={paths.Length}");
            foreach (var path in paths) Log($"PATH {path}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    internal static int WatchCycle(int seconds)
    {
        using var events = new BlockingCollection<(uint Action, string Path, DateTimeOffset At)>();
        Callback callback = (_, _, action, data, size) =>
        {
            // Copy the variable-length OS buffer; never perform USB I/O inside this callback.
            if (action <= 1 && size >= 26 && Marshal.ReadInt32(data) == 0)
            {
                var path = Marshal.PtrToStringUni(data + 24, checked((int)(size - 24) / 2))!.Split('\0')[0];
                if (IsTarget(path)) events.Add((action, path, DateTimeOffset.Now));
            }
            return 0;
        };
        IntPtr registration = IntPtr.Zero, usb = IntPtr.Zero;
        SafeFileHandle? file = null;
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool removed = false;
        void Close()
        {
            if (usb != IntPtr.Zero) { Native.WinUsb_Free(usb); usb = IntPtr.Zero; }
            file?.Dispose(); file = null;
        }
        void Open(string path)
        {
            Close();
            file = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW");
            if (!Native.WinUsb_Initialize(file, out usb)) throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_Initialize");
            if (!Native.WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor)) throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryInterfaceSettings");
            Log($"OPEN_OK endpoints={descriptor.Endpoints}");
        }
        try
        {
            var filter = new Filter { Size = (uint)Marshal.SizeOf<Filter>(), ClassGuid = interfaceClass };
            Check(CM_Register_Notification(ref filter, IntPtr.Zero, callback, out registration), "Register");
            // Register before enumeration to avoid missing a plug event during startup.
            foreach (var path in Enumerate()) { connected.Add(path); Log($"INITIAL_CONNECTED {path}"); Open(path); }
            Log($"READY connected={connected.Count > 0}; waiting for one removal/rearrival cycle, timeout={seconds}s");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                int remaining = (int)Math.Min(int.MaxValue, Math.Ceiling((seconds - timer.Elapsed.TotalSeconds) * 1000));
                if (!events.TryTake(out var item, Math.Max(1, remaining))) break;
                Log($"EVENT {(item.Action == 0 ? "ARRIVAL" : "REMOVAL")} received={item.At:O} path={item.Path}");
                if (item.Action == 1 && connected.Remove(item.Path))
                {
                    Close(); removed = true;
                    Log("DISCONNECTED handles_closed=True");
                }
                else if (item.Action == 0 && connected.Add(item.Path))
                {
                    Open(item.Path);
                    Log("CONNECTED new_handle=True");
                    if (removed)
                    {
                        Log($"CYCLE_OK present_count={Enumerate().Length}");
                        return 0;
                    }
                }
                Log($"SNAPSHOT present_count={Enumerate().Length}");
            }
            Log("TIMEOUT cycle incomplete"); return 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (registration != IntPtr.Zero) Check(CM_Unregister_Notification(registration), "Unregister");
            GC.KeepAlive(callback);
            Close();
        }
    }

    private static string[] Enumerate()
    {
        while (true)
        {
            Check(CM_Get_Device_Interface_List_SizeW(out uint length, ref interfaceClass, null, 0), "ListSize");
            var buffer = new char[length];
            uint result = CM_Get_Device_Interface_ListW(ref interfaceClass, null, buffer, length, 0);
            if (result == 0x1A) continue; // CR_BUFFER_SMALL: device list changed between the two API calls.
            Check(result, "List");
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(IsTarget).ToArray();
        }
    }

    private static void Log(string message) => Console.WriteLine($"{DateTimeOffset.Now:O} {message}");
    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new IOException($"CM {operation}: CONFIGRET=0x{result:X}");
    }

    // Union includes WCHAR InstanceId[MAX_DEVICE_ID_LEN=200], even for an interface filter.
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct Filter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(16)] public Guid ClassGuid;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Callback(IntPtr notification, IntPtr context, uint action, IntPtr eventData, uint eventDataSize);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Register_Notification(ref Filter filter, IntPtr context, Callback callback, out IntPtr notification);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Unregister_Notification(IntPtr notification);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid classGuid, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid classGuid, string? deviceId, [Out] char[] buffer, uint length, uint flags);
}
