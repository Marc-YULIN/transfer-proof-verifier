using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TransferProof.Core.Abstractions;
using TransferProof.Core.Domain;
using TransferProof.Infrastructure;

namespace TransferProof.Api.Endpoints;

public static class VerificationEndpoints
{
    public static RouteGroupBuilder MapVerifications(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/verifications").WithTags("Verifications").AddEndpointFilter<MerchantKeyFilter>();
        group.MapGet("/{id:guid}", async (Guid id, TransferProofDbContext db, CancellationToken ct) =>
        {
            var item = await db.Verifications.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, ct);
            if (item is null) return EndpointSupport.Problem(404, "verification_not_found", "Verification not found.");
            var attempts = await db.Attempts.Where(a => a.VerificationId == id).Select(a => new { a.Provider, a.DurationMs, a.Succeeded, a.FrameIndex }).ToListAsync(ct);
            var disputeId = await db.Disputes.Where(d => d.VerificationId == id).Select(d => (Guid?)d.Id).SingleOrDefaultAsync(ct);
            return Results.Ok(new { item.Id, item.Status, item.Verdict, checks = JsonSerializer.Deserialize<JsonElement>(item.ChecksJson), attempts, disputeId, item.BestFrameIndex, item.TraceId });
        }).WithSummary("Get verification status, checks, and extraction audit summaries");
        return group;
    }
}
public static class DisputeEndpoints
{
    public static RouteGroupBuilder MapDisputes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/disputes").WithTags("Disputes").AddEndpointFilter<MerchantKeyFilter>();
        group.MapGet("", async (DisputeStatus? status, TransferProofDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Disputes.Where(d => status == null || d.Status == status).ToListAsync(ct))).WithSummary("List disputes by status");
        group.MapPost("/{id:guid}/resolve", async (Guid id, TransferProofDbContext db, CancellationToken ct) =>
        {
            if (await db.Disputes.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DisputeStatus.Resolved), ct) == 0)
                return EndpointSupport.Problem(404, "dispute_not_found", "Dispute not found.");
            return Results.NoContent();
        }).WithSummary("Resolve a dispute");
        return group;
    }
}
public static class SigningEndpoints
{
    public static RouteGroupBuilder MapSigningKeys(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/.well-known").WithTags("Signing keys");
        group.MapGet("/jwks.json", (IJwsSigner signer) => signer.GetPublicJwks()).WithSummary("Get the public webhook verification key");
        return group;
    }
}
