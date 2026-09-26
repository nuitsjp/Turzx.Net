using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Turzx;

internal sealed class WinUsbConnection : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly object _lifetimeGate = new();
    private IntPtr _usb;
    internal byte InputPipe { get; }
    internal byte OutputPipe { get; }
    internal IntPtr Handle => _usb;

    private WinUsbConnection(SafeFileHandle file, IntPtr usb, byte inputPipe, byte outputPipe)
    {
        _file = file;
        _usb = usb;
        InputPipe = inputPipe;
        OutputPipe = outputPipe;
    }

    internal static WinUsbConnection Open(string path)
    {
        var file = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new Win32Exception(error, "CreateFileW");
        }
        IntPtr usb = IntPtr.Zero;
        try
        {
            Check(Native.WinUsb_Initialize(file, out usb), "WinUsb_Initialize");
            Check(Native.WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor), "WinUsb_QueryInterfaceSettings");
            byte input = 0, output = 0;
            for (byte i = 0; i < descriptor.Endpoints; i++)
            {
                Check(Native.WinUsb_QueryPipe(usb, 0, i, out var pipe), "WinUsb_QueryPipe");
                if (pipe.Type != 2) continue;
                if ((pipe.Id & 0x80) != 0) input = pipe.Id; else output = pipe.Id;
                uint timeout = 2000;
                Check(Native.WinUsb_SetPipePolicy(usb, pipe.Id, 3, 4, ref timeout), "WinUsb_SetPipePolicy");
            }
            if (input == 0 || output == 0) throw new IOException("USB bulk IN/OUT endpoints not found.");
            return new WinUsbConnection(file, usb, input, output);
        }
        catch
        {
            if (usb != IntPtr.Zero) Native.WinUsb_Free(usb);
            file.Dispose();
            throw;
        }
    }

    internal void DrainPendingResponses()
    {
        uint timeout = 100;
        Check(Native.WinUsb_SetPipePolicy(_usb, InputPipe, 3, 4, ref timeout), "Set drain timeout");
        try
        {
            var packet = new byte[512];
            bool empty = false;
            for (int i = 0; i < 16; i++)
            {
                if (!Native.WinUsb_ReadPipe(_usb, InputPipe, packet, 512, out _, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 121) throw new Win32Exception(error, "Drain USB input");
                    empty = true;
                    break;
                }
            }
            if (!empty) throw new IOException("USB input remained busy.");
        }
        finally
        {
            timeout = 2000;
            Check(Native.WinUsb_SetPipePolicy(_usb, InputPipe, 3, 4, ref timeout), "Restore read timeout");
        }
    }

    internal void Write(byte[] packet)
    {
        Check(Native.WinUsb_WritePipe(_usb, OutputPipe, packet, (uint)packet.Length, out uint written, IntPtr.Zero), "WinUsb_WritePipe");
        if (written != packet.Length) throw new IOException($"USB short write: {written}/{packet.Length} bytes.");
    }

    internal byte[] ReadResponse()
    {
        var response = new byte[512];
        uint read;
        do { Check(Native.WinUsb_ReadPipe(_usb, InputPipe, response, 512, out read, IntPtr.Zero), "WinUsb_ReadPipe"); }
        while (read == 0); // A 512-byte response can be followed by a zero-length packet.
        return response.AsSpan(0, (int)read).ToArray();
    }

    internal void Abort()
    {
        lock (_lifetimeGate)
        {
            if (_usb == IntPtr.Zero) return;
            Native.WinUsb_AbortPipe(_usb, InputPipe);
            Native.WinUsb_AbortPipe(_usb, OutputPipe);
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_usb != IntPtr.Zero) { Native.WinUsb_Free(_usb); _usb = IntPtr.Zero; }
            _file.Dispose();
        }
    }

    private static void Check(bool ok, string operation)
    {
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }
}
