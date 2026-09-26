using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

if (args.Length == 1 && args[0] == "status") return DeviceConnection.Status();
if (args.Length == 2 && args[0] == "watch-cycle" && int.TryParse(args[1], out int watchSeconds) && watchSeconds > 0)
    return DeviceConnection.WatchCycle(watchSeconds);

if (args.Length < 2 || args[0] is not ("probe" or "show" or "benchmark") ||
    (args[0] != "benchmark" && args.Length != 2) || (args[0] == "benchmark" && args.Length is not (5 or 6)))
{
    Console.Error.WriteLine("Usage: Turzx.Probe status\n       Turzx.Probe watch-cycle <timeout-seconds>\n       Turzx.Probe <probe|show> <device path>\n       Turzx.Probe benchmark <device path> <png-flat|jpeg-flat|png-cached|png-live|png-pipeline|jpeg-cached|jpeg-live|jpeg-pipeline|jpeg-detail> <seconds> <result.json> [target-fps]");
    return 2;
}
if (!args[1].Contains("vid_1cbe&pid_0092", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("This experiment only supports TURZX 9.2 (1CBE:0092).");
    return 2;
}

try
{
    using var file = Native.CreateFileW(args[1], 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
    if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileW");
    Check(Native.WinUsb_Initialize(file, out var usb), "WinUsb_Initialize");
    try
    {
        bool quiet = args[0] == "benchmark";
        bool firstExchange = true;
        uint speedLength = 1;
        var speed = new byte[1];
        Check(Native.WinUsb_QueryDeviceInformation(usb, 1, ref speedLength, speed), "QueryDeviceInformation");
        Console.WriteLine($"USB speed code={speed[0]} (3=high-speed or higher, 1=full-speed or lower)");
        Check(Native.WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor), "QueryInterfaceSettings");
        Console.WriteLine($"Interface={descriptor.Number}, endpoints={descriptor.Endpoints}");
        byte input = 0, output = 0;
        for (byte i = 0; i < descriptor.Endpoints; i++)
        {
            Check(Native.WinUsb_QueryPipe(usb, 0, i, out var pipe), "QueryPipe");
            Console.WriteLine($"Endpoint=0x{pipe.Id:X2}, type={pipe.Type}, maxPacket={pipe.MaximumPacketSize}");
            if (pipe.Type != 2) continue; // USB bulk only.
            if ((pipe.Id & 0x80) != 0) input = pipe.Id; else output = pipe.Id;
            uint timeout = 2000;
            Check(Native.WinUsb_SetPipePolicy(usb, pipe.Id, 3, 4, ref timeout), "SetPipePolicy timeout");
        }
        if (input == 0 || output == 0) throw new IOException("Bulk IN/OUT endpoints missing.");
        Exchange(10, []);
        if (args[0] == "benchmark")
            return Benchmark.Run(args[2], int.Parse(args[3]), args[4], args.Length == 6 ? double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 0, Exchange);
        if (args[0] == "show")
        {
            Directory.CreateDirectory("artifacts");
            Thread.Sleep(200);
            var timings = new List<double>();
            for (int frame = 1; frame <= 10; frame++)
            {
                using var bitmap = DrawFrame(frame);
                if (frame == 10) bitmap.Save("artifacts/test-landscape.png", ImageFormat.Png);
                bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                var png = stream.ToArray();
                if (png.Length > 1024 * 1024) throw new IOException("PNG exceeds 1 MiB protocol limit.");
                if (frame == 10) File.WriteAllBytes("artifacts/test-wire.png", png);
                timings.Add(Exchange(102, png).TotalMs);
                Thread.Sleep(500);
            }
            Console.WriteLine($"Sent 10 frames. USB exchange ms: min={timings.Min():F1}, mean={timings.Average():F1}, max={timings.Max():F1}");
            Console.WriteLine("Device responses do not prove visual correctness. Check text, corners and orientation on the panel.");
        }

        TransferResult Exchange(byte command, byte[] payload)
        {
            if (firstExchange)
            {
                // Drain earlier replies once; per-frame draining would impose a 100 ms delay.
                uint drainTimeout = 100;
                Check(Native.WinUsb_SetPipePolicy(usb, input, 3, 4, ref drainTimeout), "Set drain timeout");
                var pending = new byte[512];
                bool drained = false;
                for (int i = 0; i < 16; i++)
                {
                    if (!Native.WinUsb_ReadPipe(usb, input, pending, 512, out var pendingSize, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != 121) throw new Win32Exception(error, "Drain input");
                        drained = true;
                        break;
                    }
                    Console.WriteLine($"Drained previous USB packet: {pendingSize} bytes.");
                }
                if (!drained) throw new IOException("Input remained busy; another process may be using the device.");
                uint responseTimeout = 2000;
                Check(Native.WinUsb_SetPipePolicy(usb, input, 3, 4, ref responseTimeout), "Set response timeout");
                firstExchange = false;
            }
            var total = Stopwatch.StartNew();
            // Vendor wire format: DES-CBC encrypted command followed by unencrypted image data.
            // Protocol reference: phstudy/turing-smart-screen-cli (MIT); see THIRD-PARTY-NOTICES.md.
            var header = new byte[504]; // 500-byte command, padded to DES block size.
            header[0] = command;
            header[2] = 0x1A;
            header[3] = 0x6D;
            uint timestamp = (uint)DateTime.Now.TimeOfDay.TotalMilliseconds;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)payload.Length);
            using var des = DES.Create();
            des.Key = "slv3tuzx"u8.ToArray();
            des.IV = des.Key;
            des.Padding = PaddingMode.None;
            des.Mode = CipherMode.CBC;
            using var encryptor = des.CreateEncryptor();
            var packet = new byte[512 + payload.Length];
            encryptor.TransformFinalBlock(header, 0, header.Length).CopyTo(packet, 0);
            packet[510] = 0xA1;
            packet[511] = 0x1A;
            payload.CopyTo(packet, 512);
            var watch = Stopwatch.StartNew();
            double prepareMs = total.Elapsed.TotalMilliseconds;
            Check(Native.WinUsb_WritePipe(usb, output, packet, (uint)packet.Length, out var written, IntPtr.Zero), "WritePipe");
            if (written != packet.Length) throw new IOException($"Short write {written}/{packet.Length}");
            double writeMs = watch.Elapsed.TotalMilliseconds;
            var response = new byte[512];
            uint read;
            int zeroPackets = 0;
            // The device can terminate a previous 512-byte response with a zero-length packet.
            // Consume that transport terminator without resending the command.
            do
            {
                Check(Native.WinUsb_ReadPipe(usb, input, response, 512, out read, IntPtr.Zero), "ReadPipe");
                if (read == 0) zeroPackets++;
                if (watch.ElapsedMilliseconds > 2000) throw new TimeoutException("No response within 2 seconds.");
            } while (read == 0);
            watch.Stop();
            if (!quiet) Console.WriteLine($"cmd={command}, sent={written}, received={read}, elapsed={watch.Elapsed.TotalMilliseconds:F1}ms, response={Convert.ToHexString(response.AsSpan(0, (int)Math.Min(read, 32)))}");
            if (read < 6 || response[0] != command || response[1] != 0xC8 || BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(2)) != timestamp)
                throw new IOException("Unexpected device response; stopping without further commands.");
            return new TransferResult(prepareMs, writeMs, watch.Elapsed.TotalMilliseconds - writeMs, total.Elapsed.TotalMilliseconds, zeroPackets);
        }
    }
    finally { Native.WinUsb_Free(usb); }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    return 1;
}

static void Check(bool ok, string operation)
{
    if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
}

static Bitmap DrawFrame(int frame)
{
    var bitmap = new Bitmap(1920, 462, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.Clear(Color.FromArgb(14, 22, 36));
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    using var title = new Font("Segoe UI", 50, FontStyle.Bold);
    using var body = new Font("Yu Gothic UI", 24);
    using var small = new Font("Segoe UI", 16);
    using var cyan = new SolidBrush(Color.FromArgb(70, 215, 230));
    using var border = new Pen(Color.White, 4);
    g.DrawRectangle(border, 2, 2, 1915, 457);
    g.DrawString("TURZX / .NET USB TEST", title, Brushes.White, 55, 45);
    g.DrawString("日本語表示・1920 × 462・USB直接描画", body, cyan, 60, 140);
    g.DrawString($"FRAME {frame:00} / 10    {DateTime.Now:yyyy-MM-dd HH:mm:ss}", body, Brushes.White, 60, 208);
    Color[] colors = [Color.Red, Color.Lime, Color.Blue, Color.Cyan, Color.Magenta, Color.Yellow, Color.White];
    for (int i = 0; i < colors.Length; i++)
    {
        using var brush = new SolidBrush(colors[i]);
        g.FillRectangle(brush, 60 + i * 250, 295, 240, 65);
    }
    g.FillRectangle(cyan, 60, 382, frame * 175, 15);
    g.DrawString("TOP LEFT", small, Brushes.White, 8, 5);
    g.DrawString("TOP RIGHT", small, Brushes.White, 1760, 5);
    g.DrawString("BOTTOM LEFT", small, Brushes.White, 8, 427);
    g.DrawString("BOTTOM RIGHT", small, Brushes.White, 1710, 427);
    return bitmap;
}
