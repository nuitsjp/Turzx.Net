using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Turzx;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct InterfaceDescriptor
    {
        public byte Length, Type, Number, AlternateSetting, Endpoints, Class, SubClass, Protocol, Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PipeInformation
    {
        public int Type;
        public byte Id;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [StructLayout(LayoutKind.Explicit, Size = 416)]
    internal struct NotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint NotifyCallback(IntPtr notification, IntPtr context, uint action, IntPtr data, uint size);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    internal static extern uint CM_Register_Notification(ref NotifyFilter filter, IntPtr context, NotifyCallback callback, out IntPtr registration);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    internal static extern uint CM_Unregister_Notification(IntPtr registration);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid classGuid, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_Interface_ListW(ref Guid classGuid, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr handle);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_QueryInterfaceSettings(IntPtr handle, byte alternate, out InterfaceDescriptor descriptor);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_QueryPipe(IntPtr handle, byte alternate, byte index, out PipeInformation pipe);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint size, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, [Out] byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_AbortPipe(IntPtr handle, byte pipe);
    [DllImport("winusb.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_Free(IntPtr handle);
}
