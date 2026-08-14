using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the real-time assessment table — day-partitioned parent created by raw SQL in the
/// migration, same arrangement as the granular tables: this configuration must agree with that
/// DDL, and the composite key carries the partition column.
/// </summary>
public class RealtimeAssessmentConfiguration : IEntityTypeConfiguration<RealtimeAssessment>
{
    public void Configure(EntityTypeBuilder<RealtimeAssessment> builder)
    {
        builder.ToTable("RealtimeAssessments");

        builder.HasKey(a => new { a.CardiMemberId, a.WindowStartUtc });

        // Assessments are a few sentences by prompt design; the cap is a guard against a
        // runaway generation ever being stored, not a format the text is expected to approach.
        builder.Property(a => a.ModelOutput)
            .IsRequired()
            .HasMaxLength(4000);

        // The model's literal severity word — the parser only recognises four, but what is
        // stored is whatever it actually said, for audit.
        builder.Property(a => a.RawSeverity)
            .HasMaxLength(50);

        builder.Property(a => a.Severity)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(a => a.GeneratedAtUtc)
            .IsRequired();

        builder.Property(a => a.SsaEngine)
            .HasMaxLength(50);
    }
}
