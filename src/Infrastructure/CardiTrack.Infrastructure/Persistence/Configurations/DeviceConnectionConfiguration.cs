using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class DeviceConnectionConfiguration : IEntityTypeConfiguration<DeviceConnection>
{
    public void Configure(EntityTypeBuilder<DeviceConnection> builder)
    {
        builder.ToTable("DeviceConnections");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.CardiMemberId)
            .IsRequired();

        builder.Property(d => d.DeviceType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.DeviceName)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(d => d.IsPrimary)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(d => d.ConnectionStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        // Encrypted fields - will be encrypted by encryption service
        builder.Property(d => d.AccessToken)
            .HasMaxLength(2000);

        builder.Property(d => d.RefreshToken)
            .HasMaxLength(2000);

        builder.Property(d => d.TokenExpiry);

        // JSON field
        builder.Property(d => d.Scopes)
            .HasMaxLength(500)
            .HasDefaultValue("[]");

        builder.Property(d => d.ConnectedDate);

        builder.Property(d => d.LastSyncDate);

        builder.Property(d => d.SyncFrequencyMinutes)
            .IsRequired()
            .HasDefaultValue(10);

        builder.Property(d => d.NextPullAt);

        builder.Property(d => d.ConsecutiveEmptyPulls)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(d => d.HistoryBackfilledTo);

        builder.Property(d => d.HealthUserId)
            .HasMaxLength(100);

        // The webhook aggregator's lookup: notification → connection.
        builder.HasIndex(d => d.HealthUserId)
            .HasFilter("\"HealthUserId\" IS NOT NULL");

        // JSON field
        builder.Property(d => d.Metadata)
            .HasMaxLength(2000);

        builder.Property(d => d.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(d => d.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(d => d.UpdatedDate);

        // Indexes
        builder.HasIndex(d => d.CardiMemberId);
        builder.HasIndex(d => d.DeviceType);
        builder.HasIndex(d => d.ConnectionStatus);
        builder.HasIndex(d => new { d.CardiMemberId, d.IsPrimary });
        builder.HasIndex(d => d.LastSyncDate);

        // The pull planner's only filter once calibration writes a schedule. Partial: rows with no
        // NextPullAt fall back to the LastSyncDate path and would only bloat the index.
        builder.HasIndex(d => d.NextPullAt)
            .HasFilter("\"NextPullAt\" IS NOT NULL");

        // Ignore navigation properties
        builder.Ignore(d => d.ActivityLogs);
    }
}
