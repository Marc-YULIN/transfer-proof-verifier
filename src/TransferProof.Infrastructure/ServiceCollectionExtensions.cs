using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TransferProof.Core.Abstractions;
using TransferProof.Core.Matching;

namespace TransferProof.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTransferProofInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<SecurityOptions>().BindConfiguration("Security")
            .PostConfigure(o => { if (environment.IsDevelopment() && string.IsNullOrWhiteSpace(o.TokenKey)) o.TokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); })
            .ValidateDataAnnotations().Validate(o => ValidKey(o.TokenKey), "Security:TokenKey must be a base64 encoded 32-byte key.").ValidateOnStart();
        services.AddOptions<TokenOptions>().BindConfiguration("Tokens").ValidateDataAnnotations()
            .Validate(o => o.InstallTokenTtl > TimeSpan.Zero && o.SessionTtl > TimeSpan.Zero, "Token lifetimes must be positive.").ValidateOnStart();
        services.AddOptions<StorageOptions>().BindConfiguration("Storage").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<FfmpegOptions>().BindConfiguration("Ffmpeg").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<TesseractOptions>().BindConfiguration("Tesseract").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<GeminiOptions>().BindConfiguration("Gemini").PostConfigure(o => {
            if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        }).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ExtractionOptions>().BindConfiguration("Extraction").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<MatchingOptions>().BindConfiguration("Matching").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SigningOptions>().BindConfiguration("Signing").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<WebhookOptions>().BindConfiguration("Webhook").ValidateDataAnnotations()
            .Validate(o => string.IsNullOrWhiteSpace(o.Url) || Uri.TryCreate(o.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https", "Webhook URL must use HTTP or HTTPS.")
            .Validate(o => o.RetryDelaysSeconds.Length <= 10 && o.RetryDelaysSeconds.All(x => x >= 0), "Invalid retry delays.").ValidateOnStart();
        services.AddDbContext<TransferProofDbContext>(o => o.UseSqlite(configuration.GetConnectionString("Database") ?? "Data Source=appdata/transfer-proof-verifier.db"));
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Redis"))) services.AddSingleton<IOneTimeTokenStore, InMemoryOneTimeTokenStore>();
        else
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis")!));
            services.AddSingleton<IOneTimeTokenStore, RedisOneTimeTokenStore>();
        }
        services.AddSingleton<AesGcmTokenProtector>(); services.AddSingleton<ITokenProtector>(s => s.GetRequiredService<AesGcmTokenProtector>());
        services.AddSingleton<IFrameExtractor, FfmpegFrameExtractor>(); services.AddSingleton<IFrameScorer, SkiaFrameScorer>();
        services.AddSingleton<IOcrEngine>(s => { var engine = new TesseractCliOcrEngine(s.GetRequiredService<IOptions<TesseractOptions>>()); return engine.IsAvailable ? engine : new NullOcrEngine(); });
        services.AddHttpClient("Gemini", c => c.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(s => {
            var proxy = s.GetRequiredService<IOptions<GeminiOptions>>().Value.Proxy;
            return new HttpClientHandler { Proxy = string.IsNullOrWhiteSpace(proxy) ? null : new WebProxy(proxy), UseProxy = !string.IsNullOrWhiteSpace(proxy) };
        }).RemoveAllLoggers();
        services.AddHttpClient("Webhook", c => c.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers();
        services.AddSingleton<GeminiTransferExtractor>(); services.AddSingleton<OcrHeuristicTransferExtractor>(); services.AddSingleton<EmbeddedMetadataTransferExtractor>();
        services.AddSingleton<TransferExtractorResolver>(); services.AddSingleton<ITransferExtractor>(s => s.GetRequiredService<TransferExtractorResolver>().Extractor);
        services.AddSingleton<IJwsSigner, Es256JwsSigner>(); services.AddSingleton<IVerificationQueue, ChannelVerificationQueue>();
        services.AddScoped<IWebhookNotifier, WebhookNotifier>(); services.AddHostedService<VerificationWorker>();
        services.AddHostedService<ProviderStartup>();
        return services;
    }
    private static bool ValidKey(string key) { try { return Convert.FromBase64String(key).Length == 32; } catch (FormatException) { return false; } }
}

internal sealed class ProviderStartup(ITransferExtractor extractor, IJwsSigner signer, IConfiguration configuration, IHostEnvironment environment, ILogger<ProviderStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = extractor.Name; _ = signer.GetPublicJwks();
        if (environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["Security:TokenKey"]))
            InfrastructureLog.TemporaryTokenKey(logger);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
