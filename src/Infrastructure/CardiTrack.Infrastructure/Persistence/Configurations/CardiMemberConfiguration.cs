using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class CardiMemberConfiguration : IEntityTypeConfiguration<CardiMember>
{
    public void Configure(EntityTypeBuilder<CardiMember> builder)
    {
        builder.ToTable("CardiMembers");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.OrganizationId)
            .IsRequired();

        builder.Property(c => c.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(c => c.Email)
            .HasMaxLength(255);

        builder.Property(c => c.Phone)
            .HasMaxLength(20);

        builder.Property(c => c.DateOfBirth)
            .IsRequired();

        builder.Property(c => c.Gender)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        // CardiJournal timings. All nullable — null is "use the default", not "unset and broken",
        // so an existing member needs no backfill and the generator's fallback is the same code
        // path for a member who has never opened the setting and one who cleared it.
        builder.Property(c => c.DaybookLocalTime);
        builder.Property(c => c.WeekbookLocalTime);
        builder.Property(c => c.MonthbookLocalTime);

        builder.Property(c => c.JournalWeekStartsOn)
            .HasConversion<string>()
            .HasMaxLength(20);

        // CardiJournal comparison tolerances, nullable for the same reason the timings are. The
        // percentage is stored to one decimal: the band is read as "one percent" or "half a
        // percent", and a wider scale would offer a precision no caregiver is choosing between.
        builder.Property(c => c.DaybookBedtimeToleranceMinutes);
        builder.Property(c => c.DaybookWakeToleranceMinutes);
        builder.Property(c => c.DaybookDirectionBoundMinutes);

        builder.Property(c => c.DaybookLevelTolerancePercent)
            .HasPrecision(4, 1);

        builder.Property(c => c.EmergencyContactName)
            .HasMaxLength(200);

        builder.Property(c => c.EmergencyContactPhone)
            .HasMaxLength(20);

        // Encrypted at rest by CardiMemberService. Deliberately unbounded: the request caps
        // notes at 2000 characters, but base64 of AES-GCM ciphertext over 2000 multi-byte
        // characters runs to roughly 10k — a 2000-char column would reject valid input.
        builder.Property(c => c.MedicalNotes);

        builder.Property(c => c.LastSyncDate);

        builder.Property(c => c.MonitoringPausedUntil);

        builder.Property(c => c.MonitoringPauseReason)
            .HasMaxLength(200);

        // EF cannot infer a sentinel here: the string conversion leaves the configured default in
        // provider space, so it falls back to the CLR default and warns. Pinning it explicitly is a
        // no-op on behaviour — the sentinel is the same value EF was already assuming — but it makes
        // the intent checkable: 0 is not a member of AlertSensitivity, so the sentinel is unreachable
        // and EF always writes the column. The database default only ever covers non-EF inserts.
        builder.Property(c => c.AlertSensitivity)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasSentinel(default(Domain.Enums.AlertSensitivity))
            .HasDefaultValue(Domain.Enums.AlertSensitivity.Medium);

        // Object name in the member-photos bucket, never a URL. members/{guid}/{guid:N}.jpg is
        // 82 chars today; 256 leaves room without inviting arbitrary paths into the column.
        builder.Property(c => c.PhotoObjectName)
            .HasMaxLength(256);

        builder.Property(c => c.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

        // Default false and required: this is the sole gate on the environmental-enrichment
        // feature (data_protection_architecture.md) — a missing column value must never be read
        // as consent.
        builder.Property(c => c.EnvironmentalContextConsentGranted)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(c => c.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(c => c.UpdatedDate);

        // Indexes
        builder.HasIndex(c => c.OrganizationId);
        builder.HasIndex(c => c.IsActive);
        builder.HasIndex(c => c.LastSyncDate);
        // The sync-due query joins members and filters on the pause window every worker tick.
        builder.HasIndex(c => c.MonitoringPausedUntil);

        // Ignore navigation properties
        builder.Ignore(c => c.UserCardiMembers);
        builder.Ignore(c => c.DeviceConnections);
        builder.Ignore(c => c.ActivityLogs);
        builder.Ignore(c => c.Alerts);
        builder.Ignore(c => c.PatternBaselines);
    }
}
