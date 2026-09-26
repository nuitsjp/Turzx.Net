using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

internal readonly record struct TransferResult(double PrepareMs, double WriteMs, double ReadMs, double TotalMs, int ZeroPackets);

internal static class Benchmark
{
    private sealed record Frame(int Index, double EndSeconds, int Bytes, double DrawMs, double EncodeMs,
        double PrepareMs, double WriteMs, double ReadMs, double TotalMs, double GenerationToAckMs, int ZeroPackets);
    private sealed record Prepared(byte[] Data, double DrawMs, double EncodeMs, long StartedTimestamp);

    public static int Run(string profile, int seconds, string resultPath, double targetFps,
        Func<byte, byte[], TransferResult> exchange)
    {
        if (profile is not ("png-flat" or "jpeg-flat" or "png-cached" or "png-live" or "png-pipeline" or "jpeg-cached" or "jpeg-live" or "jpeg-pipeline" or "jpeg-detail") ||
            seconds < 1 || seconds > 600 || targetFps < 0 || targetFps > 1000)
            throw new ArgumentException("Unsupported profile, duration (1..600 s), or target FPS (0..1000).");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
        bool flat = profile.EndsWith("flat");
        bool cached = profile.EndsWith("cached") || flat;
        bool pipelined = profile.EndsWith("pipeline");
        bool jpeg = profile.StartsWith("jpeg");
        byte command = jpeg ? (byte)101 : (byte)102;
        using var background = profile == "jpeg-detail" ? DetailBackground() : null;
        var frames = new List<Frame>(seconds * 100);
        var images = new List<byte[]>();
        if (cached)
            for (int i = 0; i < 16; i++)
            {
                using var image = flat ? DrawFlat(i) : Draw(i, null);
                images.Add(Encode(image, jpeg));
            }
        // One queued frame overlaps CPU drawing/encoding with the previous frame's USB exchange.
        // Every produced frame is sent in order; no dropping and no unbounded queue.
        using var cancel = new CancellationTokenSource();
        var queue = Channel.CreateBounded<Prepared>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        Task? producer = pipelined ? Task.Run(async () =>
        {
            try
            {
                int index = 0;
                while (!cancel.IsCancellationRequested)
                {
                    var next = Prepare(index++);
                    await queue.Writer.WriteAsync(next, cancel.Token);
                }
                queue.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { queue.Writer.TryComplete(); }
            catch (Exception e) { queue.Writer.TryComplete(e); }
        }) : null;
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"BENCHMARK pid={process.Id} profile={profile}, duration={seconds}s, targetFPS={targetFps}, JPEG quality=85. Warming up 5s.");
        // JIT, GDI+ and device warm-up are excluded from the steady-state measurements.
        var warmup = Stopwatch.StartNew();
        int warmupFrames = 0;
        while (warmup.Elapsed.TotalSeconds < 5)
        {
            Send(warmupFrames++);
            if (targetFps > 0) Pace(warmup, warmupFrames, targetFps);
        }
        warmup.Stop();
        Console.WriteLine("MEASUREMENT_START");
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var userStart = process.UserProcessorTime;
        var kernelStart = process.PrivilegedProcessorTime;
        long allocatedStart = GC.GetTotalAllocatedBytes(true);
        var pauseStart = GC.GetTotalPauseDuration();
        int[] gcStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        long privateStart = process.PrivateMemorySize64;
        long workingStart = process.WorkingSet64;
        var startedAt = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        string? error = null;
        try
        {
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                var frame = Send(frames.Count);
                frames.Add(frame with { EndSeconds = watch.Elapsed.TotalSeconds });
                if (targetFps > 0) Pace(watch, frames.Count, targetFps);
            }
        }
        catch (Exception e) { error = e.ToString(); }
        watch.Stop();
        var endedAt = DateTimeOffset.UtcNow;
        process.Refresh();
        double cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        double userSeconds = (process.UserProcessorTime - userStart).TotalSeconds;
        double kernelSeconds = (process.PrivilegedProcessorTime - kernelStart).TotalSeconds;
        double elapsed = watch.Elapsed.TotalSeconds;
        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedStart;
        var collections = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - gcStart[g]).ToArray();
        double pauseMs = (GC.GetTotalPauseDuration() - pauseStart).TotalMilliseconds;
        long privateEnd = process.PrivateMemorySize64, workingEnd = process.WorkingSet64, peakWorkingSet = process.PeakWorkingSet64;
        cancel.Cancel();
        producer?.GetAwaiter().GetResult();
        var report = new
        {
            Profile = profile, ProcessId = process.Id, StartedAt = startedAt, EndedAt = endedAt,
            RequestedSeconds = seconds, ElapsedSeconds = elapsed, TargetFps = targetFps,
            WarmupSeconds = warmup.Elapsed.TotalSeconds, WarmupFrames = warmupFrames,
            SuccessfulFrames = frames.Count, FailedFrames = error is null ? 0 : 1, Error = error,
            AckFramesPerSecond = frames.Count / elapsed,
            PayloadMiBPerSecond = frames.Sum(f => (long)f.Bytes) / elapsed / 1048576,
            WireApplicationMiBPerSecond = frames.Sum(f => (long)f.Bytes + 512 + 512) / elapsed / 1048576,
            LogicalProcessors = Environment.ProcessorCount,
            CpuSeconds = cpuSeconds,
            CpuOneCorePercent = cpuSeconds / elapsed * 100,
            CpuMachinePercent = cpuSeconds / elapsed / Environment.ProcessorCount * 100,
            UserCpuSeconds = userSeconds, KernelCpuSeconds = kernelSeconds,
            PrivateBytesStart = privateStart, PrivateBytesEnd = privateEnd,
            WorkingSetStart = workingStart, WorkingSetEnd = workingEnd,
            PeakWorkingSetBytes = peakWorkingSet,
            ManagedAllocatedMiBPerSecond = allocated / elapsed / 1048576,
            GcCollections = collections, GcPauseMilliseconds = pauseMs,
            LandscapeWidth = 1920, LandscapeHeight = 462, WireWidth = 462, WireHeight = 1920,
            JpegQuality = jpeg ? 85 : (int?)null, CachedFrames = cached ? 16 : 0, Pipelined = pipelined,
            PayloadBytes = Stats(frames.Select(f => (double)f.Bytes)),
            DrawMilliseconds = Stats(frames.Select(f => f.DrawMs)),
            EncodeMilliseconds = Stats(frames.Select(f => f.EncodeMs)),
            PrepareMilliseconds = Stats(frames.Select(f => f.PrepareMs)),
            WriteMilliseconds = Stats(frames.Select(f => f.WriteMs)),
            ReadMilliseconds = Stats(frames.Select(f => f.ReadMs)),
            FrameWorkMilliseconds = Stats(frames.Select(f => f.TotalMs)),
            GenerationToAckMilliseconds = cached ? null : Stats(frames.Select(f => f.GenerationToAckMs)),
            ZeroLengthPackets = frames.Sum(f => f.ZeroPackets), Frames = frames
        };
        File.WriteAllText(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"MEASUREMENT_END {endedAt:O}: {frames.Count} ACKs / {elapsed:F3}s = {report.AckFramesPerSecond:F2} ACK fps, CPU={report.CpuMachinePercent:F3}% machine ({report.CpuOneCorePercent:F2}% one core), errors={report.FailedFrames}");
        Console.WriteLine($"Result: {Path.GetFullPath(resultPath)}");
        if (error is not null) Console.Error.WriteLine(error);
        return error is null ? 0 : 1;

        Frame Send(int index)
        {
            var frameClock = Stopwatch.StartNew();
            double drawMs = 0, encodeMs = 0;
            long generationStarted = 0;
            byte[] data;
            if (cached) data = images[index % images.Count];
            else
            {
                var prepared = pipelined ? queue.Reader.ReadAsync().AsTask().GetAwaiter().GetResult() : Prepare(index);
                data = prepared.Data;
                drawMs = prepared.DrawMs;
                encodeMs = prepared.EncodeMs;
                generationStarted = prepared.StartedTimestamp;
            }
            if (data.Length > 1024 * 1024) throw new IOException($"Encoded image exceeds 1 MiB: {data.Length}");
            var transfer = exchange(command, data);
            return new Frame(index, 0, data.Length, drawMs, encodeMs, transfer.PrepareMs,
                transfer.WriteMs, transfer.ReadMs, frameClock.Elapsed.TotalMilliseconds,
                cached ? 0 : Stopwatch.GetElapsedTime(generationStarted).TotalMilliseconds, transfer.ZeroPackets);
        }

        Prepared Prepare(int index)
        {
            long started = Stopwatch.GetTimestamp();
            var draw = Stopwatch.StartNew();
            using var image = Draw(index % 16, background);
            double drawMs = draw.Elapsed.TotalMilliseconds;
            var encoding = Stopwatch.StartNew();
            var data = Encode(image, jpeg);
            return new Prepared(data, drawMs, encoding.Elapsed.TotalMilliseconds, started);
        }
    }

    private static void Pace(Stopwatch elapsed, int frames, double fps)
    {
        double remaining = frames / fps * 1000 - elapsed.Elapsed.TotalMilliseconds;
        if (remaining > 0) Thread.Sleep(TimeSpan.FromMilliseconds(remaining));
    }

    private static object Stats(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        double Q(double q) => values.Length == 0 ? 0 : values[(int)Math.Ceiling((values.Length - 1) * q)];
        return new { Mean = values.Length == 0 ? 0 : values.Average(), Min = Q(0), P50 = Q(.5), P95 = Q(.95), P99 = Q(.99), Max = Q(1) };
    }

    private static byte[] Encode(Bitmap image, bool jpeg)
    {
        // Rotation is part of encoding time, not drawing time.
        image.RotateFlip(RotateFlipType.Rotate90FlipNone);
        using var output = new MemoryStream();
        if (jpeg)
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            image.Save(output, ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters);
        }
        else image.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static Bitmap Draw(int phase, Bitmap? background)
    {
        var image = background is null ? new Bitmap(1920, 462, PixelFormat.Format32bppArgb) : new Bitmap(background);
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (background is null) g.Clear(Color.FromArgb(14, 22, 36));
        using var title = new Font("Segoe UI", 32, FontStyle.Bold);
        using var body = new Font("Yu Gothic UI", 22);
        using var cyan = new SolidBrush(Color.FromArgb(70, 215, 230));
        using var line = new Pen(Color.FromArgb(70, 215, 230), 4);
        using var shade = new SolidBrush(Color.FromArgb(220, 14, 22, 36));
        g.FillRectangle(shade, 25, 20, 1870, 145);
        g.DrawString("TURZX / .NET PERFORMANCE TEST", title, Brushes.White, 45, 30);
        g.DrawString($"日本語ダッシュボード   PHASE {phase:00} / 15   1920 × 462", body, cyan, 45, 100);
        var points = Enumerable.Range(0, 180).Select(i => new PointF(40 + i * 10,
            280 + 65 * (float)Math.Sin((i + phase * 4) * .08))).ToArray();
        g.DrawLines(line, points);
        g.FillRectangle(cyan, 40 + phase * 112, 380, 100, 45);
        g.DrawRectangle(Pens.White, 1, 1, 1917, 459);
        // A 4-bit phase marker distinguishes cached frames as well as live frames.
        for (int bit = 0; bit < 4; bit++)
            g.FillRectangle((phase & (1 << bit)) != 0 ? Brushes.White : Brushes.Black, 1450 + bit * 90, 90, 70, 55);
        return image;
    }

    private static Bitmap DetailBackground()
    {
        var image = new Bitmap(1920, 462, PixelFormat.Format32bppArgb);
        var data = image.LockBits(new Rectangle(0, 0, 1920, 462), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * 462];
            new Random(20260927).NextBytes(pixels);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { image.UnlockBits(data); }
        return image;
    }

    private static Bitmap DrawFlat(int phase)
    {
        var image = new Bitmap(1920, 462, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        g.Clear(Color.FromArgb(14, 22, 36));
        g.FillRectangle(Brushes.CadetBlue, 40 + phase * 112, 220, 24, 24);
        return image;
    }
}
