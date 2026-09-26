using System.Drawing;
using System.Drawing.Imaging;
using Turzx;

if (args.Length > 1)
{
    Console.Error.WriteLine("Usage: dotnet run -- [device-id]");
    return 2;
}

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    var devices = (await TurzxDevices.ListAsync()).ToArray();
    var requestedDeviceId = args.FirstOrDefault();
    var selectedIndex = -1;
    if (requestedDeviceId is not null)
    {
        for (var index = 0; index < devices.Length; index++)
        {
            if (string.Equals(devices[index].DeviceId, requestedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = index;
                break;
            }
        }

        if (selectedIndex < 0)
        {
            Console.Error.WriteLine($"Device not found: {requestedDeviceId}");
            PrintDevices();
            return 2;
        }
    }
    else if (devices.Length == 1)
    {
        selectedIndex = 0;
    }
    else
    {
        Console.Error.WriteLine(devices.Length == 0
            ? "No TURZX devices found."
            : "More than one TURZX device was found. Pass one device ID as the optional argument.");
        PrintDevices();
        return 2;
    }

    var device = devices[selectedIndex];

    await using var session = new TurzxSession(device.DeviceId);
    var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var watchCycle = false;

    session.StateChanged += OnStateChanged;

    Console.WriteLine($"Device: {device.DeviceId}");
    Console.WriteLine($"Initial state: {session.State}");
    Console.WriteLine("Starting session and waiting for the initial ready state...");

    await session.StartAsync();
    await session.WaitUntilReadyAsync(shutdown.Token);

    Console.WriteLine($"{Timestamp()} Ready: {session.State}");
    Console.WriteLine("Unplug the device once, wait for the disconnect event, then plug it back in once.");
    watchCycle = true;

    await disconnected.Task.WaitAsync(shutdown.Token);
    await reconnected.Task.WaitAsync(shutdown.Token);

    Console.WriteLine($"{Timestamp()} Reconnected: {session.State}");
    Console.WriteLine("Sending one validation PNG...");
    var png = CreateValidationPng();
    await session.SendFrameAsync(TurzxFrame.FromPng(png));
    Console.WriteLine($"{Timestamp()} Validation PNG sent ({png.Length:N0} bytes).");
    return 0;

    void PrintDevices()
    {
        foreach (var listedDevice in devices)
            Console.Error.WriteLine($"  {listedDevice.DeviceId}");
    }

    void OnStateChanged(object? sender, TurzxStateChangedEventArgs eventArgs)
    {
        var error = Convert.ToString(eventArgs.Error);
        var suffix = string.IsNullOrWhiteSpace(error) ? string.Empty : $" error={error}";
        Console.WriteLine($"{Timestamp()} State: {eventArgs.OldState} -> {eventArgs.NewState}{suffix}");

        if (!watchCycle)
            return;

        if (IsDisconnected(eventArgs.NewState))
            disconnected.TrySetResult(true);
        else if (disconnected.Task.IsCompleted && IsReady(eventArgs.NewState))
            reconnected.TrySetResult(true);
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Console.WriteLine("Cancelled.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Manual test failed: {exception.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static string Timestamp() => $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}]";

static bool IsDisconnected(TurzxSessionState state) => state == TurzxSessionState.Disconnected;

static bool IsReady(TurzxSessionState state) => state == TurzxSessionState.Ready;

static byte[] CreateValidationPng()
{
    using var bitmap = new Bitmap(1920, 462);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.Clear(Color.FromArgb(14, 22, 36));
        using var border = new Pen(Color.White, 4);
        using var titleFont = new Font("Segoe UI", 42, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", 24);
        using var smallFont = new Font("Segoe UI", 16);
        using var accent = new SolidBrush(Color.FromArgb(70, 215, 230));

        graphics.DrawRectangle(border, 2, 2, 1915, 457);
        graphics.DrawString("TURZX / .NET MANUAL TEST", titleFont, Brushes.White, 55, 55);
        graphics.DrawString("日本語表示・1920 × 462・USB再接続", bodyFont, accent, 60, 155);
        graphics.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), bodyFont, Brushes.White, 60, 210);

        Color[] colors = [Color.Red, Color.Lime, Color.Blue, Color.Cyan, Color.Magenta, Color.Yellow, Color.White];
        for (var index = 0; index < colors.Length; index++)
        {
            using var brush = new SolidBrush(colors[index]);
            graphics.FillRectangle(brush, 60 + index * 250, 320, 240, 55);
        }
        graphics.DrawString("TOP LEFT", smallFont, Brushes.White, 8, 5);
        graphics.DrawString("TOP RIGHT", smallFont, Brushes.White, 1760, 5);
        graphics.DrawString("BOTTOM LEFT", smallFont, Brushes.White, 8, 427);
        graphics.DrawString("BOTTOM RIGHT", smallFont, Brushes.White, 1710, 427);
    }

    bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    return stream.ToArray();
}
