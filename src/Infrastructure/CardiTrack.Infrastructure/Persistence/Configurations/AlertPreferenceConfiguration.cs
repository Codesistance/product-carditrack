using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class AlertPreferenceConfiguration : IEntityTypeConfiguration<AlertPreference>
{
    public void Configure(EntityTypeBuilder<AlertPreference> builder)
    {
        builder.ToTable("AlertPreferences");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.CardiMemberId).IsRequired();

        builder.Property(p => p.DisabledRules)
            .IsRequired()
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb");

        builder.Property(p => p.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(p => p.UpdatedDate);

        builder.HasIndex(p => p.CardiMemberId).IsUnique();
    }
}
