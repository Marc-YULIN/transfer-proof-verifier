namespace TransferProof.Core.Domain;

public enum CaptureStatus { Issued, Uploaded, Verifying, Completed, Expired }
public enum MediaKind { Image, Video }
public enum VerificationStatus { Queued, Processing, Completed, Failed }
public enum Verdict { Matched, Mismatched, Unreadable }
public enum DisputeStatus { Open, Resolved }

public sealed class PaymentOrder
{
    public string Id { get; set; } = "";
    public string BuyerName { get; set; } = "";
    public string SellerName { get; set; } = "";
    public string SellerBank { get; set; } = "";
    public string SellerAccount { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "KRW";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CaptureSession
{
    public Guid Id { get; set; }
    public string OrderId { get; set; } = "";
    public string SessionTokenHash { get; set; } = "";
    public CaptureStatus Status { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
}

public sealed class Verification
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string OrderId { get; set; } = "";
    public MediaKind MediaKind { get; set; }
    public string StoragePath { get; set; } = "";
    public VerificationStatus Status { get; set; }
    public Verdict? Verdict { get; set; }
    public string ChecksJson { get; set; } = "[]";
    public int? BestFrameIndex { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string TraceId { get; set; } = "";
}

public sealed class ExtractionAttempt
{
    public Guid Id { get; set; }
    public Guid VerificationId { get; set; }
    public int FrameIndex { get; set; }
    public string Provider { get; set; } = "";
    public bool Succeeded { get; set; }
    public long DurationMs { get; set; }
    public string ResponseExcerpt { get; set; } = "";
    public string? Error { get; set; }
    public string TraceId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Dispute
{
    public Guid Id { get; set; }
    public string OrderId { get; set; } = "";
    public Guid VerificationId { get; set; }
    public string Reason { get; set; } = "";
    public string EvidencePath { get; set; } = "";
    public DisputeStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid VerificationId { get; set; }
    public string Url { get; set; } = "";
    public int Attempt { get; set; }
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset SentAt { get; set; }
}

public sealed record TransferExtraction(decimal? Amount, string? Currency, string? SenderName,
    string? ReceiverName, string? ReceiverBank, string? ReceiverAccount,
    DateTimeOffset? TransferredAt, bool IsCompletionScreen, double Confidence);
public sealed record FieldCheck(string Field, string? Expected, string? Actual, bool Passed, string Reason);
public sealed record MatchResult(Verdict Verdict, IReadOnlyList<FieldCheck> Checks);
