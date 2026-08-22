using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class PatternBaselineConfiguration : IEntityTypeConfiguration<PatternBaseline>
{
    public void Configure(EntityTypeBuilder<PatternBaseline> builder)
    {
        builder.ToTable("PatternBaselines");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.CardiMemberId)
            .IsRequired();

        builder.Property(p => p.CalculatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(p => p.PeriodDays)
            .IsRequired();

        // Activity Baseline Metrics
        builder.Property(p => p.AvgSteps);
        builder.Property(p => p.StdDevSteps).HasColumnType("decimal(10,2)");
        builder.Property(p => p.MedianSteps);
        builder.Property(p => p.MadSteps).HasColumnType("decimal(10,2)");
        builder.Property(p => p.AvgActiveMinutes);

        // Heart Rate Baseline Metrics
        builder.Property(p => p.AvgRestingHeartRate);
        builder.Property(p => p.StdDevHeartRate).HasColumnType("decimal(10,2)");
        builder.Property(p => p.MedianRestingHeartRate);
        builder.Property(p => p.MadHeartRate).HasColumnType("decimal(10,2)");
        builder.Property(p => p.MaxHeartRateObserved);

        // Heart Rate Variability Baseline Metrics
        builder.Property(p => p.AvgHeartRateVariabilityMs).HasColumnType("decimal(10,2)");
        builder.Property(p => p.StdDevHeartRateVariability).HasColumnType("decimal(10,2)");
        builder.Property(p => p.MedianHeartRateVariabilityMs).HasColumnType("decimal(10,2)");
        builder.Property(p => p.MadHeartRateVariability).HasColumnType("decimal(10,2)");

        // Body Baseline Metrics
        builder.Property(p => p.AvgWeightKg).HasColumnType("decimal(6,2)");

        // Sleep Baseline Metrics
        builder.Property(p => p.AvgSleepMinutes);
        builder.Property(p => p.MedianSleepMinutes);
        builder.Property(p => p.MadSleepMinutes).HasColumnType("decimal(10,2)");
        builder.Property(p => p.TypicalBedtime);
        builder.Property(p => p.TypicalWakeTime);
        builder.Property(p => p.AvgSleepEfficiency);

        // JSON field for day-of-week patterns
        builder.Property(p => p.StepsByDayOfWeek)
            .HasMaxLength(500);

        builder.Property(p => p.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(p => p.UpdatedDate);

        // Indexes
        builder.HasIndex(p => p.CardiMemberId);
        builder.HasIndex(p => p.CalculatedDate);
        builder.HasIndex(p => new { p.CardiMemberId, p.PeriodDays, p.CalculatedDate });
    }
}
