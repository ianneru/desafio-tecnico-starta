using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sabemi.Api;

public sealed class PaymentsDb(DbContextOptions<PaymentsDb> options) : DbContext(options)
{
    public DbSet<PaymentEvent> Events => Set<PaymentEvent>();
    public DbSet<ContractStatus> Contracts => Set<ContractStatus>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var events = model.Entity<PaymentEvent>();
        events.ToTable("PaymentEvents");
        events.HasKey(x => x.Id);
        events.Property(x => x.TransactionId).HasMaxLength(100);
        events.Property(x => x.ContractId).HasMaxLength(100);
        events.Property(x => x.Amount).HasPrecision(18, 2);
        events.HasIndex(x => x.TransactionId).IsUnique().HasFilter("\"Canonical\" = TRUE");
        events.HasIndex(x => new { x.ProcessingStatus, x.NextAttemptAt });
        events.HasIndex(x => new { x.ContractId, x.ReceivedAt });
        events.HasOne<PaymentEvent>().WithMany().HasForeignKey(x => x.OriginalEventId).OnDelete(DeleteBehavior.Restrict);
        var contracts = model.Entity<ContractStatus>();
        contracts.ToTable("ContractStatuses");
        contracts.HasKey(x => x.ContractId);
        contracts.Property(x => x.ContractId).HasMaxLength(100);
        contracts.Property(x => x.Amount).HasPrecision(18, 2);
    }
}

public sealed class PaymentsDbFactory : IDesignTimeDbContextFactory<PaymentsDb>
{
    public PaymentsDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PaymentsDb>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Payments") ??
            "Host=localhost;Database=sabemi;Username=sabemi").Options);
}