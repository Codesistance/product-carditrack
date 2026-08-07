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

        builder.Property(c => c.AlertSensitivity)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(Domain.Enums.AlertSensitivity.Medium);

        builder.Property(c => c.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

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
