using Microsoft.EntityFrameworkCore;
using TransferProof.Core.Domain;

namespace TransferProof.Infrastructure;

public sealed class TransferProofDbContext(DbContextOptions<TransferProofDbContext> options) : DbContext(options)
{
    public DbSet<PaymentOrder> Orders => Set<PaymentOrder>();
    public DbSet<CaptureSession> Sessions => Set<CaptureSession>();
    public DbSet<Verification> Verifications => Set<Verification>();
    public DbSet<ExtractionAttempt> Attempts => Set<ExtractionAttempt>();
    public DbSet<Dispute> Disputes => Set<Dispute>();
    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentOrder>().Property(x => x.Amount).HasConversion(
            value => checked((long)(value * 10000m)), value => value / 10000m);
        modelBuilder.Entity<CaptureSession>().HasIndex(x => x.SessionTokenHash).IsUnique();
        modelBuilder.Entity<CaptureSession>().HasOne<PaymentOrder>().WithMany().HasForeignKey(x => x.OrderId);
        modelBuilder.Entity<Verification>().HasIndex(x => x.OrderId);
        modelBuilder.Entity<Verification>().HasOne<CaptureSession>().WithMany().HasForeignKey(x => x.SessionId);
        modelBuilder.Entity<Verification>().HasOne<PaymentOrder>().WithMany().HasForeignKey(x => x.OrderId);
        modelBuilder.Entity<ExtractionAttempt>().HasOne<Verification>().WithMany().HasForeignKey(x => x.VerificationId);
        modelBuilder.Entity<Dispute>().HasIndex(x => x.OrderId);
        modelBuilder.Entity<Dispute>().HasIndex(x => x.VerificationId).IsUnique();
        modelBuilder.Entity<Dispute>().HasOne<Verification>().WithMany().HasForeignKey(x => x.VerificationId);
        modelBuilder.Entity<WebhookDelivery>().HasOne<Verification>().WithMany().HasForeignKey(x => x.VerificationId);
    }
}
