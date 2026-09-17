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

        // Postgres's own row version as the concurrency token. The disabled-rule list is one JSON
        // value read, modified and written whole, and it now has two writers — the settings page
        // and a confirmed chat change — so a save from a read another commit has overtaken is
        // refused (DbUpdateConcurrencyException) rather than restoring the rule it flipped.
        builder.Property<uint>("xmin").IsRowVersion();

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
