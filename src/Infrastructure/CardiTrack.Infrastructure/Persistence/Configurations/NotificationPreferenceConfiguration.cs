using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("NotificationPreferences");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.UserId).IsRequired();

        builder.Property(p => p.QuietHoursStart);
        builder.Property(p => p.QuietHoursEnd);

        builder.Property(p => p.ShowDetailsOnLockScreen)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(p => p.MutedCategories)
            .IsRequired()
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb");

        builder.Property(p => p.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(p => p.UpdatedDate);

        builder.HasIndex(p => p.UserId).IsUnique();
    }
}
