using Microsoft.EntityFrameworkCore;
using TransferProof.Core.Domain;
using TransferProof.Infrastructure;

namespace TransferProof.Api.Endpoints;

public sealed record CreateOrderRequest(string Id, string BuyerName, string SellerName, string SellerBank, string SellerAccount, decimal Amount, string Currency);
public static class OrderEndpoints
{
    public static RouteGroupBuilder MapOrders(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders").WithTags("Orders").AddEndpointFilter<MerchantKeyFilter>();
        group.MapPost("", async (CreateOrderRequest request, TransferProofDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (new[] { request.Id, request.BuyerName, request.SellerName, request.SellerBank, request.SellerAccount }.Any(string.IsNullOrWhiteSpace)
                || request.Id.Length > 128 || request.Amount <= 0 || request.Amount > long.MaxValue / 10000m
                || decimal.Round(request.Amount, 4) != request.Amount || !System.Text.RegularExpressions.Regex.IsMatch(request.Currency ?? "", "^[A-Za-z]{3}$"))
                return EndpointSupport.Problem(400, "order_invalid", "Required fields, a positive amount with at most four decimal places, and a three-letter currency are required.");
            var order = new PaymentOrder { Id = request.Id, BuyerName = request.BuyerName, SellerName = request.SellerName,
                SellerBank = request.SellerBank, SellerAccount = request.SellerAccount, Amount = request.Amount,
                Currency = request.Currency!.ToUpperInvariant(), CreatedAt = time.GetUtcNow() };
            db.Orders.Add(order);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
            { return EndpointSupport.Problem(409, "order_exists", "The order already exists."); }
            return Results.Created($"/api/orders/{Uri.EscapeDataString(order.Id)}", order);
        }).WithSummary("Create a payment order");
        group.MapGet("/{id}", async (string id, TransferProofDbContext db, CancellationToken ct) =>
            await db.Orders.FindAsync([id], ct) is { } order ? Results.Ok(order) : EndpointSupport.Problem(404, "order_not_found", "Order not found.")).WithSummary("Get a payment order");
        group.MapGet("/{id}/verifications", async (string id, TransferProofDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Verifications.Where(v => v.OrderId == id).Select(v => new { v.Id, v.Status, v.Verdict, v.CreatedAt }).ToListAsync(ct))).WithSummary("List order verifications");
        return group;
    }
}
