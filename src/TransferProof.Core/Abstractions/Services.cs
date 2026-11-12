using TransferProof.Core.Domain;

namespace TransferProof.Core.Abstractions;

public interface IOneTimeTokenStore
{
    Task StoreAsync(string key, string valueHash, TimeSpan ttl, CancellationToken cancellationToken);
    /// <summary>
    /// Atomically deletes <paramref name="key"/> only when it still holds <paramref name="expectedHash"/>.
    /// A stale or forged value never removes the current entry, so replaying an old link cannot burn the latest one.
    /// </summary>
    Task<bool> TryConsumeAsync(string key, string expectedHash, CancellationToken cancellationToken);
    Task MarkUsedAsync(string tokenHash, TimeSpan ttl, CancellationToken cancellationToken);
    Task<bool> WasUsedAsync(string tokenHash, CancellationToken cancellationToken);
}
public sealed record InstallTokenPayload(string OrderId, string Nonce, DateTimeOffset ExpiresAt);
public interface ITokenProtector
{
    string Protect(InstallTokenPayload payload);
    bool TryUnprotect(string token, out InstallTokenPayload? payload);
}
public interface IFrameExtractor
{
    Task<IReadOnlyList<string>> ExtractAsync(string videoPath, string outputDir, CancellationToken cancellationToken);
}
public sealed record FrameScore(string Path, int Index, double LumaVariance, double EdgeDensity,
    bool IsLikelyLoadingScreen, double Score);
public interface IFrameScorer { FrameScore Score(string imagePath); }
public interface IOcrEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    Task<string?> ReadTextAsync(string imagePath, CancellationToken cancellationToken);
}
public sealed record ExtractionInput(string ImagePath, string? OcrText, string Hint);
public interface ITransferExtractor
{
    string Name { get; }
    Task<TransferExtraction?> ExtractAsync(ExtractionInput input, CancellationToken cancellationToken);
}
public sealed record JsonWebKey(string kty, string crv, string x, string y, string kid, string alg, string use);
public sealed record JsonWebKeySet(IReadOnlyList<JsonWebKey> keys);
public interface IJwsSigner
{
    string Sign(object payload);
    JsonWebKeySet GetPublicJwks();
}
public interface IWebhookNotifier
{
    Task NotifyAsync(Verification verification, Guid? disputeId, CancellationToken cancellationToken);
}
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The contract explicitly represents a verification queue.")]
public interface IVerificationQueue
{
    ValueTask EnqueueAsync(Guid id);
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}
