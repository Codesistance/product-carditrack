using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class DeviceHistoryRepullConfiguration : IEntityTypeConfiguration<DeviceHistoryRepull>
{
    /// <summary>
    /// The statuses that count as "open" for the one-open-request-per-connection rule. Spelled
    /// out as the stored names because the partial index filter below is raw SQL: EF cannot
    /// notice a rename of <see cref="HistoryRepullStatus"/> here, so a rename must change this
    /// too or the uniqueness guarantee silently stops applying.
    /// </summary>
    private const string OpenStatusFilter = "\"Status\" IN ('Pending', 'InProgress')";

    public void Configure(EntityTypeBuilder<DeviceHistoryRepull> builder)
    {
        builder.ToTable("DeviceHistoryRepulls");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.DeviceConnectionId).IsRequired();
        builder.Property(r => r.CardiMemberId).IsRequired();
        builder.Property(r => r.RequestedByUserId).IsRequired();

        builder.Property(r => r.FromDate).IsRequired();
        builder.Property(r => r.ToDate).IsRequired();
        builder.Property(r => r.CompletedTo);
        builder.Property(r => r.DaysWithData).IsRequired();
        builder.Property(r => r.Attempts).IsRequired();

        // Enums persist as names throughout this schema; a human reading a stuck request sees
        // "InProgress", not 2.
        builder.Property(r => r.Status).IsRequired().HasConversion<string>().HasMaxLength(20);

        builder.Property(r => r.RequestedAt).IsRequired();
        builder.Property(r => r.StartedAt);
        builder.Property(r => r.CompletedAt);
        builder.Property(r => r.FailureReason).HasMaxLength(200);

        builder.Property(r => r.CreatedDate).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(r => r.UpdatedDate);

        // At most one open request per connection. The service checks before inserting, but a
        // caregiver double-tapping — or two caregivers tapping at once — is a race the check
        // cannot close; the index can, and the insert that loses surfaces as REPULL_IN_PROGRESS.
        builder.HasIndex(r => r.DeviceConnectionId)
            .IsUnique()
            .HasFilter(OpenStatusFilter)
            .HasDatabaseName("IX_DeviceHistoryRepulls_OneOpenPerConnection");

        // The Worker's due read: open rows, oldest request first.
        builder.HasIndex(r => r.RequestedAt)
            .HasFilter(OpenStatusFilter)
            .HasDatabaseName("IX_DeviceHistoryRepulls_Due");

        // The device list's latest-per-connection read, and the cooldown's last-completed read.
        builder.HasIndex(r => new { r.DeviceConnectionId, r.RequestedAt })
            .IsDescending(false, true);

        // The future "View Sync History" screen reads by member.
        builder.HasIndex(r => r.CardiMemberId);
    }
}
