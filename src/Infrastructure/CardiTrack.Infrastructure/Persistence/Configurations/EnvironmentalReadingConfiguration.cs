using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the environmental-reading table — day-partitioned parent created by raw SQL in the
/// migration, same arrangement as <c>RealtimeAssessmentConfiguration</c>: this configuration
/// must agree with that DDL, and the composite key carries the partition column.
/// </summary>
public class EnvironmentalReadingConfiguration : IEntityTypeConfiguration<EnvironmentalReading>
{
    public void Configure(EntityTypeBuilder<EnvironmentalReading> builder)
    {
        builder.ToTable("EnvironmentalReadings");

        builder.HasKey(r => new { r.CardiMemberId, r.SessionStartUtc });

        builder.Property(r => r.SessionEndUtc)
            .IsRequired();

        builder.Property(r => r.DeviceConnectionId)
            .IsRequired();

        builder.Property(r => r.AirQualityCategory)
            .HasMaxLength(50);

        // Capped because it is free text from an external service: a provider that starts returning
        // a sentence should cost a truncated column, not an unbounded one.
        builder.Property(r => r.WeatherCondition)
            .HasMaxLength(100);

        builder.Property(r => r.GeneratedAtUtc)
            .IsRequired();
    }
}
