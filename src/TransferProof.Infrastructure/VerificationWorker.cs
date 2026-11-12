using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransferProof.Core.Abstractions;
using TransferProof.Core.Domain;
using TransferProof.Core.Matching;

namespace TransferProof.Infrastructure;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The queue name follows its domain contract.")]
public sealed class ChannelVerificationQueue : IVerificationQueue
{
    private readonly Channel<Guid> channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });
    public ValueTask EnqueueAsync(Guid id) => channel.Writer.WriteAsync(id);
    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken) => channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class VerificationWorker(IServiceScopeFactory scopes, IVerificationQueue queue, ILogger<VerificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TransferProofDbContext>();
                if (await db.Verifications.Where(v => v.Id == id && v.Status == VerificationStatus.Queued)
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VerificationStatus.Processing), stoppingToken) == 0) continue;
                await ProcessAsync(id, scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                InfrastructureLog.VerificationFailed(logger, id, ex.GetType().Name);
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TransferProofDbContext>();
                    await db.Verifications.Where(v => v.Id == id && v.Status == VerificationStatus.Processing)
                        .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VerificationStatus.Failed), stoppingToken);
                }
                catch (Exception failure) { InfrastructureLog.FailurePersistenceFailed(logger, id, failure.GetType().Name); }
            }
        }
    }

    private static async Task ProcessAsync(Guid id, IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TransferProofDbContext>();
        var time = services.GetRequiredService<TimeProvider>();
        var storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        var extraction = services.GetRequiredService<IOptions<ExtractionOptions>>().Value;
        var verification = await db.Verifications.SingleAsync(v => v.Id == id, cancellationToken);
        var order = await db.Orders.SingleAsync(o => o.Id == verification.OrderId, cancellationToken);
        var session = await db.Sessions.SingleAsync(s => s.Id == verification.SessionId, cancellationToken);
        session.Status = CaptureStatus.Verifying; await db.SaveChangesAsync(cancellationToken);
        var original = Path.Combine(storage.Root, verification.StoragePath);
        var temporary = Path.Combine(Path.GetTempPath(), "tpv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        using var activity = new Activity("VerifyTransfer").Start();
        try
        {
            var paths = verification.MediaKind == MediaKind.Video
                ? await services.GetRequiredService<IFrameExtractor>().ExtractAsync(original, temporary, cancellationToken)
                : new[] { original };
            var scorer = services.GetRequiredService<IFrameScorer>();
            var selected = FrameSelector.Select(paths.Select(scorer.Score).ToArray(), extraction.MaxFrames);
            var ocr = services.GetRequiredService<IOcrEngine>();
            var extractor = services.GetRequiredService<ITransferExtractor>();
            var successes = new List<(TransferExtraction Result, FrameScore Frame)>();
            foreach (var frame in selected)
            {
                var watch = Stopwatch.StartNew();
                var attempt = new ExtractionAttempt { Id = Guid.NewGuid(), VerificationId = id, FrameIndex = frame.Index,
                    Provider = extractor.Name, TraceId = Activity.Current?.TraceId.ToString() ?? verification.TraceId, CreatedAt = time.GetUtcNow() };
                try
                {
                    var text = ocr.IsAvailable ? await ocr.ReadTextAsync(frame.Path, cancellationToken) : null;
                    var result = await extractor.ExtractAsync(new(frame.Path, text, order.Currency), cancellationToken);
                    attempt.Succeeded = result is not null;
                    if (result is not null)
                    {
                        successes.Add((result, frame));
                        var masked = result with { ReceiverAccount = AccountMatcher.Mask(result.ReceiverAccount) };
                        var excerpt = JsonSerializer.Serialize(masked);
                        attempt.ResponseExcerpt = excerpt[..Math.Min(2000, excerpt.Length)];
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { attempt.Error = ex.GetType().Name; }
                attempt.DurationMs = watch.ElapsedMilliseconds;
                db.Attempts.Add(attempt); await db.SaveChangesAsync(cancellationToken);
            }
            static int Completeness(TransferExtraction r) => new object?[] { r.Amount, r.Currency, r.SenderName, r.ReceiverName, r.ReceiverBank, r.ReceiverAccount, r.TransferredAt }.Count(x => x is not null && x is not string { Length: 0 });
            var best = successes.OrderByDescending(x => Completeness(x.Result)).ThenByDescending(x => x.Result.Confidence).FirstOrDefault();
            var match = ProofMatcher.Match(order, best.Result, services.GetRequiredService<IOptions<MatchingOptions>>().Value, time.GetUtcNow());
            var evidence = verification.StoragePath;
            if (best.Frame is not null)
            {
                evidence = $"proofs/{session.Id}/best.png";
                if (verification.MediaKind == MediaKind.Video) File.Copy(best.Frame.Path, Path.Combine(storage.Root, evidence), true);
                else evidence = verification.StoragePath;
                verification.BestFrameIndex = best.Frame.Index;
            }
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                if (match.Verdict != Verdict.Matched)
                {
                    db.Disputes.Add(new Dispute { Id = Guid.NewGuid(), OrderId = order.Id, VerificationId = id,
                        Reason = match.Verdict.ToString(), EvidencePath = evidence, CreatedAt = time.GetUtcNow(), Status = DisputeStatus.Open });
                }
                verification.Verdict = match.Verdict; verification.ChecksJson = JsonSerializer.Serialize(match.Checks);
                verification.Status = VerificationStatus.Completed; verification.CompletedAt = time.GetUtcNow(); session.Status = CaptureStatus.Completed;
                await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            }
            var disputeId = await db.Disputes.Where(d => d.VerificationId == id).Select(d => (Guid?)d.Id).SingleOrDefaultAsync(cancellationToken);
            await services.GetRequiredService<IWebhookNotifier>().NotifyAsync(verification, disputeId, cancellationToken);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
}
