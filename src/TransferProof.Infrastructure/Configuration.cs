using System.ComponentModel.DataAnnotations;

namespace TransferProof.Infrastructure;

public sealed class SecurityOptions
{
    public string TokenKey { get; set; } = "";
    [Required] public string MerchantApiKey { get; set; } = "";
}
public sealed class TokenOptions
{
    public TimeSpan InstallTokenTtl { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromMinutes(30);
}
public sealed class StorageOptions
{
    [Required] public string Root { get; set; } = "storage";
    [Range(1, 1_073_741_824)] public long MaxUploadBytes { get; set; } = 50 * 1024 * 1024;
}
public sealed class FfmpegOptions
{
    [Required] public string Path { get; set; } = "ffmpeg";
    [Range(1, 600)] public int ScanTailSeconds { get; set; } = 60;
    [Range(1, 10)] public int FramesPerSecond { get; set; } = 2;
    [Range(1, 600)] public int TimeoutSeconds { get; set; } = 60;
    [Range(0, 65025)] public double LoadingVarianceThreshold { get; set; } = 120;
}
public sealed class TesseractOptions { [Required] public string Path { get; set; } = "tesseract"; }
public sealed class GeminiOptions
{
    [Url] public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com";
    [Required] public string Model { get; set; } = "gemini-2.5-flash";
    public string ApiKey { get; set; } = "";
    public string? Proxy { get; set; }
}
public sealed class ExtractionOptions
{
    [RegularExpression("^(Auto|Gemini|OcrHeuristic|Embedded)$")] public string Provider { get; set; } = "Auto";
    [Range(1, 20)] public int MaxFrames { get; set; } = 3;
}
public sealed class SigningOptions { public string PrivateKeyPemPath { get; set; } = "appdata/signing-key.pem"; }
public sealed class WebhookOptions
{
    public string? Url { get; set; }
    public int[] RetryDelaysSeconds { get; set; } = [1, 4, 9];
}
