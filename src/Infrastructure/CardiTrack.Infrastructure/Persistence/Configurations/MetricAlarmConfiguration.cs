using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MetricAlarmConfiguration : IEntityTypeConfiguration<MetricAlarm>
{
    public void Configure(EntityTypeBuilder<MetricAlarm> builder)
    {
        builder.ToTable("MetricAlarms");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.OrganizationId).IsRequired();

        // Nullable by design: null is the account-level default every member inherits. It is the
        // scope discriminator, not a missing value.
        builder.Property(a => a.CardiMemberId);
        builder.Property(a => a.DerivedFromAlarmId);

        builder.Property(a => a.Name).IsRequired().HasMaxLength(80);

        // Enums persist as names throughout this schema: a name survives an incident and an enum
        // renumbering, and these are read by humans triaging an alarm that fired oddly.
        builder.Property(a => a.Metric).IsRequired().HasConversion<string>().HasMaxLength(40);
        builder.Property(a => a.Statistic).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.Operator).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(a => a.ThresholdKind).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.MissingDataTreatment).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.Severity).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.ContextGate).IsRequired().HasConversion<string>().HasMaxLength(20);

        // Scale 2 is enough for every unit in the catalogue — bpm, %, minutes, breaths, and the
        // sigma multipliers. Storing it as numeric rather than double keeps a threshold a caregiver
        // typed from coming back as 119.99999.
        builder.Property(a => a.ThresholdValue).IsRequired().HasPrecision(10, 2);

        builder.Property(a => a.PeriodMinutes).IsRequired();
        builder.Property(a => a.EvaluationPeriods).IsRequired();
        builder.Property(a => a.DatapointsToAlarm).IsRequired();

        builder.Property(a => a.IsEnabled).IsRequired().HasDefaultValue(true);
        builder.Property(a => a.IsActive).IsRequired().HasDefaultValue(true);

        builder.Property(a => a.CreatedDate).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(a => a.UpdatedDate);

        // The engine's read: account defaults plus one member's rows, for one organization.
        builder.HasIndex(a => new { a.OrganizationId, a.CardiMemberId });

        // Resolving overrides, and finding what a deleted account default leaves behind.
        builder.HasIndex(a => a.DerivedFromAlarmId);

        // One live override per member per account default, enforced by the database rather than by
        // the read-then-insert in MetricAlarmService. Two requests can both see no override and both
        // insert; the second row is then invisible — resolution groups by the default and takes the
        // first — but it outlives a delete, so the alarm a caregiver removed reappears. The filter
        // carries IsActive so that deleting an override and writing a new one is still allowed, and
        // it is named because an unnamed HasIndex over the same properties silently replaces the
        // plain index above rather than adding to it.
        builder.HasIndex(a => new { a.CardiMemberId, a.DerivedFromAlarmId })
            .IsUnique()
            .HasFilter("\"DerivedFromAlarmId\" IS NOT NULL AND \"IsActive\"")
            .HasDatabaseName("IX_MetricAlarms_OneOverridePerMemberPerDefault");
    }
}
