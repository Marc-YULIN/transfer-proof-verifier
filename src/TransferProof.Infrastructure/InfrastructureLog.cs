using Microsoft.Extensions.Logging;

namespace TransferProof.Infrastructure;

internal static partial class InfrastructureLog
{
    [LoggerMessage(1, LogLevel.Warning, "Created a local signing key; retain it across restarts.")]
    public static partial void SigningKeyCreated(ILogger logger);
    [LoggerMessage(2, LogLevel.Warning, "Using a temporary development token key; install links will not survive a restart.")]
    public static partial void TemporaryTokenKey(ILogger logger);
    [LoggerMessage(3, LogLevel.Warning, "Frame extraction failed: {Details}")]
    public static partial void FrameExtractionFailed(ILogger logger, string details);
    [LoggerMessage(4, LogLevel.Information, "Using extraction provider {Provider}")]
    public static partial void ProviderSelected(ILogger logger, string provider);
    [LoggerMessage(5, LogLevel.Error, "Verification {VerificationId} failed with {ErrorType}")]
    public static partial void VerificationFailed(ILogger logger, Guid verificationId, string errorType);
    [LoggerMessage(6, LogLevel.Error, "Could not persist failure for {VerificationId}: {ErrorType}")]
    public static partial void FailurePersistenceFailed(ILogger logger, Guid verificationId, string errorType);
}
