using ExpressRepair.Leads.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ExpressRepair.Leads.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Call> Calls => Set<Call>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no timezone-aware type and returns DateTime with Kind=Unspecified.
        // We always store UTC, so mark values as UTC on the way out — otherwise the JSON has no "Z"
        // and the browser would treat the time as local.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Call>(call =>
        {
            call.HasKey(c => c.Id);

            // Twilio retries webhooks: the unique index makes the endpoint idempotent even under a race.
            call.HasIndex(c => c.RecordingSid).IsUnique();
            call.HasIndex(c => c.ReceivedAtUtc);
            call.HasIndex(c => c.Status);

            call.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            call.Property(c => c.CallSid).HasMaxLength(64);
            call.Property(c => c.RecordingSid).HasMaxLength(64);
            call.Property(c => c.From).HasMaxLength(32);
            call.Property(c => c.To).HasMaxLength(32);
            call.Property(c => c.RecordingUrl).HasMaxLength(2048);
            call.Property(c => c.Appliance).HasMaxLength(16);
            call.Property(c => c.Urgency).HasMaxLength(16);
            call.Property(c => c.Error).HasMaxLength(1000);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        toDb => toDb.Kind == DateTimeKind.Utc ? toDb : toDb.ToUniversalTime(),
        fromDb => DateTime.SpecifyKind(fromDb, DateTimeKind.Utc));
}
