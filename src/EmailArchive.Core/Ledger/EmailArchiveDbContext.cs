using Microsoft.EntityFrameworkCore;

namespace EmailArchive.Core.Ledger;

public sealed class EmailArchiveDbContext(DbContextOptions<EmailArchiveDbContext> options) : DbContext(options)
{
    public DbSet<LedgerMessage> Messages => Set<LedgerMessage>();
    public DbSet<LedgerRun> Runs => Set<LedgerRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LedgerMessage>(e =>
        {
            e.ToTable("Messages");
            e.HasIndex(m => m.Key).IsUnique();
            e.Property(m => m.Key).HasMaxLength(512);
            e.Property(m => m.KeyType).HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.State).HasConversion<string>().HasMaxLength(24);
            e.Property(m => m.TargetFolder).HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.FromAddress).HasMaxLength(512);
            e.Property(m => m.Subject).HasMaxLength(2048);
            e.HasIndex(m => m.State);
        });

        modelBuilder.Entity<LedgerRun>(e =>
        {
            e.ToTable("Runs");
            e.Property(r => r.Mode).HasConversion<string>().HasMaxLength(16);
        });
    }
}
