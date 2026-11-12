using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using TransferProof.Core.Abstractions;

namespace TransferProof.Infrastructure;

public static class ExternalProcess
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string file, IEnumerable<string> arguments, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }
}

public sealed class FfmpegFrameExtractor(IOptions<FfmpegOptions> options, ILogger<FfmpegFrameExtractor> logger) : IFrameExtractor
{
    public async Task<IReadOnlyList<string>> ExtractAsync(string videoPath, string outputDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDir);
        var settings = options.Value;
        var result = await ExternalProcess.RunAsync(settings.Path,
            ["-nostdin", "-y", "-sseof", "-" + settings.ScanTailSeconds.ToString(CultureInfo.InvariantCulture), "-i", videoPath,
             "-vf", $"fps={settings.FramesPerSecond}", System.IO.Path.Combine(outputDir, "frame_%04d.png")], settings.TimeoutSeconds, cancellationToken);
        if (result.ExitCode != 0)
        {
            InfrastructureLog.FrameExtractionFailed(logger, result.Error[..Math.Min(1000, result.Error.Length)]);
            throw new InvalidOperationException("Frame extraction failed.");
        }
        return Directory.GetFiles(outputDir, "frame_*.png").Order(StringComparer.Ordinal).ToArray();
    }
}

public sealed class SkiaFrameScorer(IOptions<FfmpegOptions> options) : IFrameScorer
{
    public FrameScore Score(string imagePath)
    {
        using var original = SKBitmap.Decode(imagePath) ?? throw new InvalidDataException("Invalid image.");
        var height = Math.Clamp((int)(original.Height * (256d / original.Width)), 3, 4096);
        using var small = new SKBitmap(256, height);
        using (var canvas = new SKCanvas(small)) canvas.DrawBitmap(original, new SKRect(0, 0, 256, height), new SKSamplingOptions(SKFilterMode.Linear));
        var gray = new double[256 * height];
        for (var y = 0; y < height; y++) for (var x = 0; x < 256; x++)
        {
            var c = small.GetPixel(x, y); gray[y * 256 + x] = c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114;
        }
        var mean = gray.Average(); var variance = gray.Average(v => (v - mean) * (v - mean)); var edges = 0;
        for (var y = 1; y < height - 1; y++) for (var x = 1; x < 255; x++)
        {
            var i = y * 256 + x;
            var gx = -gray[i-257] + gray[i-255] - 2*gray[i-1] + 2*gray[i+1] - gray[i+255] + gray[i+257];
            var gy = -gray[i-257] - 2*gray[i-256] - gray[i-255] + gray[i+255] + 2*gray[i+256] + gray[i+257];
            if (Math.Sqrt(gx * gx + gy * gy) > 100) edges++;
        }
        var density = edges / (double)((height - 2) * 254);
        var suffix = System.IO.Path.GetFileNameWithoutExtension(imagePath).Split('_').Last();
        var index = int.TryParse(suffix, out var parsed) ? parsed : 0;
        return new(imagePath, index, variance, density, variance < options.Value.LoadingVarianceThreshold || density < 0.01,
            Math.Min(variance / 65025d, 1) * 0.4 + density * 0.6);
    }
}

public sealed class NullOcrEngine : IOcrEngine
{
    public string Name => "None";
    public bool IsAvailable => false;
    public Task<string?> ReadTextAsync(string imagePath, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
public sealed class TesseractCliOcrEngine : IOcrEngine
{
    private readonly TesseractOptions settings;
    public TesseractCliOcrEngine(IOptions<TesseractOptions> options)
    {
        settings = options.Value;
        try { IsAvailable = ExternalProcess.RunAsync(settings.Path, ["--version"], 5, CancellationToken.None).GetAwaiter().GetResult().ExitCode == 0; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException) { IsAvailable = false; }
    }
    public string Name => "Tesseract";
    public bool IsAvailable { get; }
    public async Task<string?> ReadTextAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return null;
        var result = await ExternalProcess.RunAsync(settings.Path, [imagePath, "stdout", "-l", "eng", "--psm", "6"], 30, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException("OCR process failed.");
        return result.Output;
    }
}
