using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Turzx;

namespace Turzx.Net.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TurzxNetTests
{
    private const string TurzxVidPid = "VID_1CBE&PID_0092";

    [TestMethod]
    public async Task ListAsync_FindsConnectedTurzx92Device()
    {
        IReadOnlyList<TurzxDeviceInfo> devices = await TurzxDevices.ListAsync();

        TurzxDeviceInfo? device = devices.FirstOrDefault(candidate =>
            candidate.DeviceId.Contains(TurzxVidPid, StringComparison.OrdinalIgnoreCase));

        Assert.IsNotNull(device, $"ListAsync did not find a {TurzxVidPid} device.");
        Assert.AreEqual(1920, device.Width);
        Assert.AreEqual(462, device.Height);
        Assert.IsTrue(device.DisplayName.Contains("TURZX", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Session_StartsReadyAndAcceptsPngAndJpegFrames()
    {
        IReadOnlyList<TurzxDeviceInfo> devices = await TurzxDevices.ListAsync();
        TurzxDeviceInfo device = devices.FirstOrDefault(candidate =>
                candidate.DeviceId.Contains(TurzxVidPid, StringComparison.OrdinalIgnoreCase))
            ?? throw new AssertFailedException($"ListAsync did not find a {TurzxVidPid} device.");

        await using var session = new TurzxSession(device.DeviceId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var changes = new ConcurrentQueue<TurzxSessionState>();
        var readyEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, change) =>
        {
            changes.Enqueue(change.NewState);
            if (change.NewState == TurzxSessionState.Ready) readyEvent.TrySetResult();
        };

        await session.StartAsync();
        await session.WaitUntilReadyAsync(timeout.Token);
        await readyEvent.Task.WaitAsync(timeout.Token);
        Assert.AreEqual(TurzxSessionState.Ready, session.State);
        CollectionAssert.AreEqual(
            new[] { TurzxSessionState.Disconnected, TurzxSessionState.Connecting, TurzxSessionState.Ready },
            changes.ToArray());

        // The payload is portrait after the required clockwise rotation and is
        // encoded once in each format. Completion of SendFrameAsync means that
        // the device acknowledgement was received.
        byte[] png = EncodeTestImage(ImageFormat.Png);
        await session.SendFrameAsync(TurzxFrame.FromPng(png));
        Assert.AreEqual(TurzxSessionState.Ready, session.State);

        byte[] jpeg = EncodeTestImage(ImageFormat.Jpeg);
        await session.SendFrameAsync(TurzxFrame.FromJpeg(jpeg));
        Assert.AreEqual(TurzxSessionState.Ready, session.State);
    }

    [TestMethod]
    public void FrameFactories_PreserveEncodingAndWireDimensions()
    {
        TurzxFrame png = TurzxFrame.FromPng(EncodeTestImage(ImageFormat.Png));
        TurzxFrame jpeg = TurzxFrame.FromJpeg(EncodeTestImage(ImageFormat.Jpeg));

        Assert.AreEqual(TurzxFrameEncoding.Png, png.Encoding);
        Assert.AreEqual(TurzxFrameEncoding.Jpeg, jpeg.Encoding);
        Assert.AreEqual(462, png.Width);
        Assert.AreEqual(1920, png.Height);
        Assert.AreEqual(462, jpeg.Width);
        Assert.AreEqual(1920, jpeg.Height);
    }

    [TestMethod]
    public void FrameFactories_RejectMalformedOrWrongDimensionPayloads()
    {
        Assert.Throws<ArgumentException>(() => TurzxFrame.FromPng(new byte[] { 0, 1, 2 }));
        Assert.Throws<ArgumentException>(() => TurzxFrame.FromJpeg(new byte[] { 0, 1, 2 }));

        byte[] oneByOnePng = EncodeTestImage(ImageFormat.Png, width: 1, height: 1, rotate: false);
        byte[] oneByOneJpeg = EncodeTestImage(ImageFormat.Jpeg, width: 1, height: 1, rotate: false);
        Assert.Throws<ArgumentException>(() => TurzxFrame.FromPng(oneByOnePng));
        Assert.Throws<ArgumentException>(() => TurzxFrame.FromJpeg(oneByOneJpeg));
    }

    [TestMethod]
    public void FrameFactories_RejectEmptyOrOversizedPayloads()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TurzxFrame.FromPng([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TurzxFrame.FromJpeg(new byte[1024 * 1024 + 1]));
    }

    [TestMethod]
    public async Task ListAsync_HonorsCancellationBeforeEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => TurzxDevices.ListAsync(cancellation.Token));
    }

    private static byte[] EncodeTestImage(ImageFormat format, int width = 1920, int height = 462, bool rotate = true)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(14, 22, 36));
            if (width == 1920 && height == 462)
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using var title = new Font("Segoe UI", 50, FontStyle.Bold);
                using var body = new Font("Yu Gothic UI", 24);
                using var small = new Font("Segoe UI", 16);
                using var accent = new SolidBrush(Color.FromArgb(70, 215, 230));
                using var border = new Pen(Color.White, 4);
                graphics.DrawRectangle(border, 2, 2, 1915, 457);
                graphics.DrawString("TURZX / .NET USB TEST", title, Brushes.White, 55, 45);
                graphics.DrawString("日本語表示・1920 × 462・USB直接描画", body, accent, 60, 140);
                graphics.DrawString($"{format} / FRAME OK    {DateTime.Now:yyyy-MM-dd HH:mm:ss}", body, Brushes.White, 60, 208);
                Color[] colors = [Color.Red, Color.Lime, Color.Blue, Color.Cyan, Color.Magenta, Color.Yellow, Color.White];
                for (int i = 0; i < colors.Length; i++)
                {
                    using var brush = new SolidBrush(colors[i]);
                    graphics.FillRectangle(brush, 60 + i * 250, 295, 240, 65);
                }
                graphics.DrawString("TOP LEFT", small, Brushes.White, 8, 5);
                graphics.DrawString("TOP RIGHT", small, Brushes.White, 1760, 5);
                graphics.DrawString("BOTTOM LEFT", small, Brushes.White, 8, 427);
                graphics.DrawString("BOTTOM RIGHT", small, Brushes.White, 1710, 427);
            }
            else graphics.DrawRectangle(Pens.White, 0, 0, width - 1, height - 1);
        }

        if (rotate)
            bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
        using var output = new MemoryStream();
        if (format.Guid == ImageFormat.Jpeg.Guid)
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            bitmap.Save(output, ImageCodecInfo.GetImageEncoders().Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid), parameters);
        }
        else bitmap.Save(output, format);
        return output.ToArray();
    }
}
