using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TransferProof.Core.Abstractions;
using TransferProof.Core.Domain;
using TransferProof.Infrastructure;

namespace TransferProof.Api.Endpoints;

public sealed record CreateLinkRequest(string OrderId);
public sealed record ExchangeRequest(string Token);
public static class CaptureLinkEndpoints
{
    public static RouteGroupBuilder MapCaptureLinks(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/capture-links").WithTags("Capture links").AddEndpointFilter<MerchantKeyFilter>();
        group.MapPost("", async (CreateLinkRequest request, TransferProofDbContext db, ITokenProtector protector,
            IOneTimeTokenStore store, IOptions<TokenOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            if (!await db.Orders.AnyAsync(o => o.Id == request.OrderId, ct)) return EndpointSupport.Problem(404, "order_not_found", "Order not found.");
            var ttl = options.Value.InstallTokenTtl; var expiresAt = time.GetUtcNow() + ttl;
            var token = protector.Protect(new(request.OrderId, TokenEncoding.Encode(RandomNumberGenerator.GetBytes(32)), expiresAt));
            await store.StoreAsync("capture:" + request.OrderId, TokenEncoding.Hash(token), ttl, ct);
            return Results.Ok(new { deepLink = "proofcapture://start?token=" + Uri.EscapeDataString(token), expiresAt });
        }).WithSummary("Issue a one-time capture link");
        return group;
    }
}

public static class CaptureSessionEndpoints
{
    public static RouteGroupBuilder MapCaptureSessions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/capture-sessions").WithTags("Capture sessions");
        group.MapPost("/exchange", ExchangeAsync).WithSummary("Exchange a one-time link for an upload session");
        group.MapPost("/{sessionId:guid}/proof", UploadAsync).WithSummary("Upload an image or video proof");
        return group;
    }
    private static async Task<IResult> ExchangeAsync(ExchangeRequest request, AesGcmTokenProtector protector,
        IOneTimeTokenStore store, TransferProofDbContext db, IOptions<TokenOptions> options, TimeProvider time, CancellationToken ct)
    {
        if (!protector.TryReadAuthenticated(request.Token, out var payload)) return EndpointSupport.Problem(401, "token_invalid", "The capture token is invalid.");
        var now = time.GetUtcNow();
        if (payload!.ExpiresAt <= now) return EndpointSupport.Problem(401, "token_expired", "The capture token expired.");
        var hash = TokenEncoding.Hash(request.Token);
        if (await store.WasUsedAsync(hash, ct)) return EndpointSupport.Problem(401, "token_used", "The capture token was already used.");
        if (!await store.TryConsumeAsync("capture:" + payload.OrderId, hash, ct))
            return await store.WasUsedAsync(hash, ct) ? EndpointSupport.Problem(401, "token_used", "The capture token was already used.")
                : EndpointSupport.Problem(401, "token_superseded", "The capture link is no longer current.");
        await store.MarkUsedAsync(hash, payload.ExpiresAt - now, ct);
        var sessionToken = TokenEncoding.Encode(RandomNumberGenerator.GetBytes(32));
        var session = new CaptureSession { Id = Guid.NewGuid(), OrderId = payload.OrderId, SessionTokenHash = TokenEncoding.Hash(sessionToken),
            IssuedAt = now, ExpiresAt = now + options.Value.SessionTtl, Status = CaptureStatus.Issued };
        db.Sessions.Add(session); await db.SaveChangesAsync(ct);
        return Results.Ok(new { sessionId = session.Id, sessionToken, session.OrderId, session.ExpiresAt });
    }
    private static async Task<IResult> UploadAsync(Guid sessionId, HttpRequest request, TransferProofDbContext db,
        IOptions<StorageOptions> options, IVerificationQueue queue, TimeProvider time, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        var token = request.Headers["X-Session-Token"].ToString();
        if (session is null || string.IsNullOrEmpty(token) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(session.SessionTokenHash), Convert.FromHexString(TokenEncoding.Hash(token))))
            return EndpointSupport.Problem(401, "session_invalid", "A valid upload session is required.");
        if (session.ExpiresAt <= time.GetUtcNow())
        {
            await db.Sessions.Where(s => s.Id == sessionId && s.Status == CaptureStatus.Issued).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CaptureStatus.Expired), ct);
            return EndpointSupport.Problem(401, "session_expired", "The upload session expired.");
        }
        if (session.Status != CaptureStatus.Issued) return EndpointSupport.Problem(409, "session_already_used", "This session already has a proof.");
        if (!request.HasFormContentType) return EndpointSupport.Problem(400, "file_required", "A multipart file is required.");
        var form = await request.ReadFormAsync(ct); var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return EndpointSupport.Problem(400, "file_required", "A nonempty file is required.");
        var settings = options.Value;
        if (file.Length > settings.MaxUploadBytes) return EndpointSupport.Problem(413, "file_too_large", "The proof exceeds the upload limit.");
        var extension = file.ContentType switch { "image/png" => "png", "image/jpeg" => "jpg", "video/mp4" => "mp4", "video/webm" => "webm", _ => null };
        if (extension is null) return EndpointSupport.Problem(415, "media_type_invalid", "Unsupported proof media type.");
        await using var input = file.OpenReadStream();
        var header = new byte[12]; var read = await input.ReadAtLeastAsync(header, 12, false, ct);
        var magic = extension switch
        {
            "png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "jpg" => read >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255,
            "mp4" => read >= 12 && header.AsSpan(4, 4).SequenceEqual("ftyp"u8),
            "webm" => read >= 4 && header.AsSpan(0, 4).SequenceEqual(new byte[] { 26, 69, 223, 163 }), _ => false
        };
        if (!magic) return EndpointSupport.Problem(415, "media_signature_invalid", "The file signature does not match the declared media type.");
        var relative = $"proofs/{sessionId}/original.{extension}"; var path = Path.Combine(settings.Root, relative);
        var verification = new Verification { Id = Guid.NewGuid(), OrderId = session.OrderId, SessionId = sessionId,
            MediaKind = extension is "png" or "jpg" ? MediaKind.Image : MediaKind.Video, StoragePath = relative,
            Status = VerificationStatus.Queued, CreatedAt = time.GetUtcNow(), TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N") };
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (await db.Sessions.Where(s => s.Id == sessionId && s.Status == CaptureStatus.Issued).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, CaptureStatus.Uploaded).SetProperty(x => x.UploadedAt, (DateTimeOffset?)time.GetUtcNow()), ct) == 0)
                return EndpointSupport.Problem(409, "session_already_used", "This session already has a proof.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                { await output.WriteAsync(header.AsMemory(0, read), ct); await input.CopyToAsync(output, ct); }
                db.Verifications.Add(verification); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            }
            catch { if (File.Exists(path)) File.Delete(path); throw; }
        }
        await queue.EnqueueAsync(verification.Id);
        return Results.Accepted($"/api/verifications/{verification.Id}", new { verificationId = verification.Id, statusUrl = $"/api/verifications/{verification.Id}" });
    }
}
