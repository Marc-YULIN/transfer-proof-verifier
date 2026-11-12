using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TransferProof.Core.Abstractions;
using TransferProof.Core.Domain;

namespace TransferProof.Infrastructure;

public sealed class WebhookNotifier(IHttpClientFactory clients, IJwsSigner signer, IOptions<WebhookOptions> options,
    TransferProofDbContext db, TimeProvider time) : IWebhookNotifier
{
    public async Task NotifyAsync(Verification verification, Guid? disputeId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Url)) return;
        var payload = new { verificationId = verification.Id, orderId = verification.OrderId, verdict = verification.Verdict.ToString(),
            checks = JsonSerializer.Deserialize<JsonElement>(verification.ChecksJson), disputeId, iat = time.GetUtcNow().ToUnixTimeSeconds() };
        var jws = signer.Sign(payload);
        using var client = clients.CreateClient("Webhook");
        for (var attempt = 0; attempt <= settings.RetryDelaysSeconds.Length; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(settings.RetryDelaysSeconds[attempt - 1]), time, cancellationToken);
            var delivery = new WebhookDelivery { Id = Guid.NewGuid(), VerificationId = verification.Id, Url = settings.Url, Attempt = attempt + 1, SentAt = time.GetUtcNow() };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url) { Content = JsonContent.Create(new { jws }) };
                request.Headers.Add("X-Signature-Kid", signer.GetPublicJwks().keys[0].kid);
                using var response = await client.SendAsync(request, cancellationToken);
                delivery.StatusCode = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode) delivery.Error = "http_error";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { delivery.Error = ex.GetType().Name; }
            db.Deliveries.Add(delivery); await db.SaveChangesAsync(cancellationToken);
            if (delivery.StatusCode is >= 200 and < 300) return;
        }
    }
}
