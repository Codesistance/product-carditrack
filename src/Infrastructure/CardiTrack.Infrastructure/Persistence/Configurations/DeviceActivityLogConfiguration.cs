using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class DeviceActivityLogConfiguration : IEntityTypeConfiguration<DeviceActivityLog>
{
    public void Configure(EntityTypeBuilder<DeviceActivityLog> builder)
    {
        builder.ToTable("DeviceActivityLogs");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.CardiMemberId)
            .IsRequired();

        builder.Property(a => a.DeviceConnectionId)
            .IsRequired();

        builder.Property(a => a.DataSource)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(a => a.Date)
            .IsRequired();

        // Activity Metrics
        builder.Property(a => a.Steps);
        builder.Property(a => a.Distance).HasColumnType("decimal(10,2)");
        builder.Property(a => a.ActiveMinutes);
        builder.Property(a => a.SedentaryMinutes);
        builder.Property(a => a.Floors);
        builder.Property(a => a.CaloriesBurned);

        // Heart Rate Metrics
        builder.Property(a => a.RestingHeartRate);
        builder.Property(a => a.AvgHeartRate);
        builder.Property(a => a.MaxHeartRate);
        builder.Property(a => a.MinHeartRate);

        // Sleep Metrics
        builder.Property(a => a.SleepMinutes);
        builder.Property(a => a.SleepStartTime);
        builder.Property(a => a.SleepEndTime);
        builder.Property(a => a.SleepEfficiency);
        builder.Property(a => a.DeepSleepMinutes);
        builder.Property(a => a.LightSleepMinutes);
        builder.Property(a => a.RemSleepMinutes);
        builder.Property(a => a.AwakeMinutes);

        // Additional Health Metrics
        builder.Property(a => a.SpO2Average).HasColumnType("decimal(5,2)");
        builder.Property(a => a.SpO2Min).HasColumnType("decimal(5,2)");
        builder.Property(a => a.SpO2Max).HasColumnType("decimal(5,2)");
        builder.Property(a => a.VO2Max).HasColumnType("decimal(5,2)");
        builder.Property(a => a.StressScore);
        builder.Property(a => a.BreathingRate).HasColumnType("decimal(5,2)");
        builder.Property(a => a.Temperature).HasColumnType("decimal(5,2)");

        builder.Property(a => a.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(a => a.UpdatedDate);

        // One raw row per device per day — this is the grain the sync upserts on.
        builder.HasIndex(a => new { a.DeviceConnectionId, a.Date }).IsUnique();

        // The merge reads every device's row for one member-day.
        builder.HasIndex(a => new { a.CardiMemberId, a.Date });
    }
}
