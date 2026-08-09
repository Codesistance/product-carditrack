using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the per-member hourly rollup table — month-partitioned parent created by raw SQL in the
/// migration, same arrangement as <see cref="GranularMetricHourConfiguration"/>.
/// </summary>
public class MetricRollupHourlyConfiguration : IEntityTypeConfiguration<MetricRollupHourly>
{
    public void Configure(EntityTypeBuilder<MetricRollupHourly> builder)
    {
        builder.ToTable("MetricRollupsHourly");

        builder.HasKey(r => new { r.CardiMemberId, r.Metric, r.HourStartUtc });

        builder.Property(r => r.Metric)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(r => r.Min).IsRequired();
        builder.Property(r => r.Max).IsRequired();
        builder.Property(r => r.Avg).IsRequired();
        builder.Property(r => r.Sum).IsRequired();
        builder.Property(r => r.SampleCount).IsRequired();
    }
}
