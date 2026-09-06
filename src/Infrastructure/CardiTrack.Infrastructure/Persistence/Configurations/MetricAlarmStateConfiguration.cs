using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MetricAlarmStateConfiguration : IEntityTypeConfiguration<MetricAlarmState>
{
    public void Configure(EntityTypeBuilder<MetricAlarmState> builder)
    {
        builder.ToTable("MetricAlarmStates");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.MetricAlarmId).IsRequired();
        builder.Property(s => s.CardiMemberId).IsRequired();

        builder.Property(s => s.State).IsRequired().HasConversion<string>().HasMaxLength(20);

        builder.Property(s => s.StateSinceUtc).IsRequired();
        builder.Property(s => s.LastEvaluatedUtc).IsRequired();
        builder.Property(s => s.LastAlertId);

        builder.Property(s => s.CreatedDate).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(s => s.UpdatedDate);

        // One state per alarm per member. Unique because a duplicate would let one tick read a
        // stale row and re-raise an alert the other row already says is standing — the exact
        // double-page this table exists to prevent.
        builder.HasIndex(s => new { s.MetricAlarmId, s.CardiMemberId }).IsUnique();

        // The engine's per-member read.
        builder.HasIndex(s => s.CardiMemberId);
    }
}
