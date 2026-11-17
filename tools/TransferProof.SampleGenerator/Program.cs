using System.Diagnostics;
using System.Globalization;
using SkiaSharp;
using TransferProof.Core.Domain;

var values = new Dictionary<string, string>(StringComparer.Ordinal);
var flags = new HashSet<string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--video" or "--no-metadata" or "--loading-only") flags.Add(args[i]);
    else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) { var key = args[i]; values[key] = args[++i]; }
    else throw new ArgumentException("Invalid arguments.");
}
string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
var output = Get("--out", "samples");
Directory.CreateDirectory(output);
var name = Get("--name", "transfer-matched");
if (Path.GetFileName(name) != name) throw new ArgumentException("Name must be a file base name.");
var at = DateTimeOffset.Parse(Get("--time", TimeProvider.System.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
var extraction = new TransferExtraction(decimal.Parse(Get("--amount", "150000"), CultureInfo.InvariantCulture), Get("--currency", "TWD"),
    Get("--sender", "Alex Chen"), Get("--receiver", "Jamie Lin"), Get("--bank", "Demo Bank"), Get("--account", "110-234-567890"), at, true, 0.99);
byte[] Render(bool loading)
{
    using var bitmap = new SKBitmap(720, 1280);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);
    using var paint = new SKPaint { IsAntialias = true };
    using var font = new SKFont(SKTypeface.Default, 34);
    void Text(string text, float x, float y, float size, SKColor color)
    {
        font.Size = size; paint.Color = color; canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
    }
    if (loading) Text("Loading...", 260, 640, 30, SKColors.LightGray);
    else
    {
        paint.Color = new SKColor(24, 52, 82); canvas.DrawRect(0, 0, 720, 120, paint);
        Text("Demo Bank", 40, 78, 44, SKColors.White);
        paint.Color = new SKColor(35, 150, 105); canvas.DrawCircle(360, 230, 55, paint);
        using (var tick = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 10, IsAntialias = true })
        { canvas.DrawLine(333, 230, 352, 250, tick); canvas.DrawLine(352, 250, 390, 208, tick); }
        Text("Transfer completed", 110, 345, 48, SKColors.Black);
        Text("Amount: " + extraction.Currency + " " + extraction.Amount!.Value.ToString("N2", CultureInfo.InvariantCulture), 40, 440, 40, SKColors.Black);
        var digits = new string(extraction.ReceiverAccount!.Where(char.IsAsciiDigit).ToArray());
        var masked = digits.Length > 6 ? digits[..3] + "-***-" + digits[^6..] : "***" + digits;
        var rows = new[] { "To: " + extraction.ReceiverName, "Bank: " + extraction.ReceiverBank, "Account: " + masked,
            "From: " + extraction.SenderName, "Date: " + at.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), "Reference: SAMPLE-001" };
        for (var row = 0; row < rows.Length; row++)
        {
            Text(rows[row], 40, 565 + row * 95, 31, SKColors.Black);
            paint.Color = SKColors.LightGray; canvas.DrawLine(40, 590 + row * 95, 680, 590 + row * 95, paint);
        }
        Text("Synthetic demonstration receipt", 80, 1220, 28, SKColors.Gray);
    }
    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
}
var image = Render(flags.Contains("--loading-only"));
if (!flags.Contains("--no-metadata") && !flags.Contains("--loading-only")) image = PngMetadata.Write(image, extraction);
await File.WriteAllBytesAsync(Path.Combine(output, name + ".png"), image);
Console.WriteLine("Created sample PNG.");
if (flags.Contains("--video"))
{
    var temporary = Path.Combine(Path.GetTempPath(), "tpv-sample-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        var loading = Render(true); var completed = Render(false);
        for (var frame = 0; frame < 50; frame++) await File.WriteAllBytesAsync(Path.Combine(temporary, $"frame_{frame:0000}.png"), frame < 20 ? loading : completed);
        using var process = new Process { StartInfo = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "-nostdin", "-y", "-framerate", "10", "-i", Path.Combine(temporary, "frame_%04d.png"), "-c:v", "libx264", "-pix_fmt", "yuv420p", Path.GetFullPath(Path.Combine(output, name + ".mp4")) }) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw; }
            _ = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Video encoding failed.");
            Console.WriteLine("Created sample MP4.");
        }
        catch (System.ComponentModel.Win32Exception) { Console.WriteLine("ffmpeg was not found; video generation skipped."); }
    }
    finally { Directory.Delete(temporary, true); }
}
