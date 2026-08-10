using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class NotificationMuteConfiguration : IEntityTypeConfiguration<NotificationMute>
{
    public void Configure(EntityTypeBuilder<NotificationMute> builder)
    {
        builder.ToTable("NotificationMutes");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.UserId).IsRequired();

        builder.Property(m => m.RuleCode).HasMaxLength(64);

        builder.Property(m => m.Category)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(m => m.CardiMemberId);

        builder.Property(m => m.MutedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(m => m.MutedUntil);

        builder.Property(m => m.AcknowledgedConsequence)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(m => m.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(m => m.UpdatedDate);

        // Muting the same scope twice is the same mute. Postgres treats NULLs as distinct in a
        // unique index, so the nullable columns get a COALESCE-free guard via the filtered indexes
        // below rather than one index that would silently permit duplicates.
        builder.HasIndex(m => new { m.UserId, m.RuleCode, m.CardiMemberId })
            .IsUnique()
            .HasFilter("\"RuleCode\" IS NOT NULL AND \"CardiMemberId\" IS NOT NULL");

        builder.HasIndex(m => new { m.UserId, m.RuleCode })
            .IsUnique()
            .HasFilter("\"RuleCode\" IS NOT NULL AND \"CardiMemberId\" IS NULL");

        builder.HasIndex(m => new { m.UserId, m.Category })
            .IsUnique()
            .HasFilter("\"Category\" IS NOT NULL");

        // Every reconciliation run loads one user's mutes before evaluating anything.
        builder.HasIndex(m => m.UserId);
    }
}
