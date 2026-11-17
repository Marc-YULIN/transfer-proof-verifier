using System.Net;
using SkiaSharp;
using TransferProof.Core.Domain;

namespace TransferProof.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset current = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan duration) => current += duration;
}
public sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tpv-test-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
public static class TestMedia
{
    public static byte[] Png(TransferExtraction? extraction = null, bool loading = false)
    {
        using var bitmap = new SKBitmap(720, 1280); using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.White);
        if (!loading)
        {
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var font = new SKFont(SKTypeface.Default, 40);
            for (var y = 100; y < 1200; y += 65) canvas.DrawText("Transfer completed 150,000", 20, y, SKTextAlign.Left, font, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return extraction is null ? data.ToArray() : PngMetadata.Write(data.ToArray(), extraction);
    }
    public static TransferExtraction Extraction(DateTimeOffset at, decimal amount = 150000) =>
        new(amount, "TWD", "Alex Chen", "Jamie Lin", "Demo Bank", "110-***-567890", at, true, 0.99);
    public static PaymentOrder Order(DateTimeOffset at) => new() { Id = "sample-001", BuyerName = "Alex Chen", SellerName = "Jamie Lin",
        SellerBank = "Demo Bank", SellerAccount = "110-234-567890", Amount = 150000, Currency = "TWD", CreatedAt = at };
}
public sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
