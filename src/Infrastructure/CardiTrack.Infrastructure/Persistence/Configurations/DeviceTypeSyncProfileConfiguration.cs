using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class DeviceTypeSyncProfileConfiguration : IEntityTypeConfiguration<DeviceTypeSyncProfile>
{
    public void Configure(EntityTypeBuilder<DeviceTypeSyncProfile> builder)
    {
        builder.ToTable("DeviceTypeSyncProfiles");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.DeviceType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(p => p.SampleSize)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(p => p.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(p => p.UpdatedDate);

        // One profile per device type — calibration recomputes a row in place rather than
        // appending, so the unique index is what keeps a failed run from leaving two.
        builder.HasIndex(p => p.DeviceType).IsUnique();
    }
}
