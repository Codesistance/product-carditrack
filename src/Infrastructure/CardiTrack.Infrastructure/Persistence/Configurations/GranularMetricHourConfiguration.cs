using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the per-device minute-vector table. The table itself is created as a range-partitioned
/// parent by raw SQL in the migration (EF cannot express <c>PARTITION BY</c>); this configuration
/// only has to agree with that DDL — column types, the composite key containing the partition
/// column, and the member-window read index.
/// </summary>
public class GranularMetricHourConfiguration : IEntityTypeConfiguration<GranularMetricHour>
{
    public void Configure(EntityTypeBuilder<GranularMetricHour> builder)
    {
        builder.ToTable("GranularMetricHours");

        // The partition key must be part of the primary key — PostgreSQL enforces it.
        builder.HasKey(g => new { g.DeviceConnectionId, g.Metric, g.HourStartUtc });

        builder.Property(g => g.Metric)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(g => g.CardiMemberId)
            .IsRequired();

        builder.Property(g => g.Values)
            .IsRequired()
            .HasColumnType("real[]");

        builder.Property(g => g.SampleCount)
            .IsRequired();

        // The window read: all metrics for one member over an hour range.
        builder.HasIndex(g => new { g.CardiMemberId, g.HourStartUtc });
    }
}
