using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class CaregiverInviteConfiguration : IEntityTypeConfiguration<CaregiverInvite>
{
    public void Configure(EntityTypeBuilder<CaregiverInvite> builder)
    {
        builder.ToTable("CaregiverInvites");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.CardiMemberId).IsRequired();
        builder.Property(i => i.OrganizationId).IsRequired();
        builder.Property(i => i.CreatedByUserId).IsRequired();

        builder.Property(i => i.Role)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(i => i.CanViewHealthData)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(i => i.ReceiveAlerts)
            .IsRequired()
            .HasDefaultValue(true);

        // 64 hex characters of SHA-256.
        builder.Property(i => i.TokenHash)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(i => i.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(i => i.ExpiresAt).IsRequired();
        builder.Property(i => i.OpenedAt);
        builder.Property(i => i.ResolvedAt);
        builder.Property(i => i.AcceptedByUserId);

        builder.Property(i => i.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(i => i.UpdatedDate);

        // Redemption's only lookup, and the one an attacker would be probing: unique so it is a
        // single indexed read with nothing whose duration could vary with how close a guess was.
        builder.HasIndex(i => i.TokenHash).IsUnique();

        builder.HasIndex(i => i.CardiMemberId);
        builder.HasIndex(i => i.OrganizationId);
    }
}
