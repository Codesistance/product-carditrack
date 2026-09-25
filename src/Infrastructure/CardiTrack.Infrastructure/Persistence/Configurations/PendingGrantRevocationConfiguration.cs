using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class PendingGrantRevocationConfiguration : IEntityTypeConfiguration<PendingGrantRevocation>
{
    public void Configure(EntityTypeBuilder<PendingGrantRevocation> builder)
    {
        builder.ToTable("PendingGrantRevocations");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.CardiMemberId).IsRequired();
        builder.Property(r => r.DeviceConnectionId).IsRequired();
        builder.Property(r => r.DeviceType).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(r => r.HealthUserId).HasMaxLength(100);
        builder.Property(r => r.Token).IsRequired();
        builder.Property(r => r.Attempts).IsRequired();
        builder.Property(r => r.NextAttemptAt).IsRequired();

        builder.Property(r => r.CreatedDate).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(r => r.UpdatedDate);

        // The Worker's due read.
        builder.HasIndex(r => r.NextAttemptAt);
        // Member erasure ends a member's queued grants before deleting them.
        builder.HasIndex(r => r.CardiMemberId);
    }
}
