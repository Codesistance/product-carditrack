using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class PushDeviceTokenConfiguration : IEntityTypeConfiguration<PushDeviceToken>
{
    public void Configure(EntityTypeBuilder<PushDeviceToken> builder)
    {
        builder.ToTable("PushDeviceTokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.UserId).IsRequired();
        builder.Property(t => t.DeviceId).IsRequired().HasMaxLength(128);

        builder.Property(t => t.Platform)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(t => t.AppVersion).IsRequired().HasMaxLength(32);

        // Ciphertext envelope ("v1:<keyfingerprint>:<b64>") — see IEncryptionService. Never a
        // plaintext token, per data_protection_architecture.md's Tier 1 classification.
        builder.Property(t => t.Token).IsRequired().HasMaxLength(1024);

        builder.Property(t => t.TokenFingerprint).IsRequired().HasMaxLength(64);

        builder.Property(t => t.OsAuthorizationStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(t => t.SafetyChannelEnabled)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(t => t.LastSeenDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(t => t.LastAckDate);
        builder.Property(t => t.DisabledDate);
        builder.Property(t => t.DisabledReason).HasMaxLength(256);

        builder.Property(t => t.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(t => t.UpdatedDate);

        // One row per install; re-registering the same device upserts rather than duplicates.
        builder.HasIndex(t => new { t.UserId, t.DeviceId }).IsUnique();

        // The upsert/lookup key — ciphertext is non-deterministic, so lookups go by fingerprint.
        builder.HasIndex(t => t.TokenFingerprint).IsUnique();

        // 30-day hard-delete sweep (§7.2 C2 / §13).
        builder.HasIndex(t => t.DisabledDate);
    }
}
