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

        builder.Property(c => c.JournalAudience)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(c => c.PolicyVersion).HasMaxLength(80).IsRequired();
        builder.Property(c => c.PolicySha256).HasMaxLength(64).IsRequired();
        builder.Property(c => c.RequestFingerprint).HasMaxLength(64).IsRequired();

        builder.Property(c => c.CreatedDate).HasDefaultValueSql("NOW()");
    }
}
