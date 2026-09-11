using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class ExportConsentConfiguration : IEntityTypeConfiguration<ExportConsent>
{
    public void Configure(EntityTypeBuilder<ExportConsent> builder)
    {
        builder.ToTable("ExportConsents");

        builder.HasKey(c => c.Id);

        builder.HasIndex(c => new { c.OwnerUserId, c.Id });
        builder.HasIndex(c => c.ExpiresAt);

        builder.Property(c => c.CardiMemberIds)
            .HasColumnType("uuid[]")
            .IsRequired();

        builder.Property(c => c.Format)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(c => c.Method)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(c => c.RememberFor)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(c => c.JournalAudience)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(c => c.PolicyVersion).HasMaxLength(80).IsRequired();
        builder.Property(c => c.PolicySha256).HasMaxLength(64).IsRequired();
        builder.Property(c => c.RequestFingerprint).HasMaxLength(64).IsRequired();

        builder.HasIndex(c => new { c.OwnerUserId, c.RememberUntil });

        // At most one live standing grant per caregiver. RecordAsync revokes then
        // inserts in a transaction, but two concurrent remembered confirmations can
        // both pass the revoke and both insert; this index is the written-once contract.
        // The filter cannot use now() — expired rows stay in the index until
        // RevokeActiveStandingAsync stamps RevokedAt, including those whose
        // RememberUntil has passed, so the next insert has a free slot.
        builder.HasIndex(c => c.OwnerUserId)
            .IsUnique()
            .HasFilter("\"ReusedFromConsentId\" IS NULL AND \"RevokedAt\" IS NULL AND \"RememberUntil\" IS NOT NULL")
            .HasDatabaseName("IX_ExportConsents_OneStandingGrant");

        builder.Property(c => c.CreatedDate).HasDefaultValueSql("NOW()");
    }
}
