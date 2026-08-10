using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the digest table — month-partitioned parent created by raw SQL in the migration, same
/// arrangement as the granular tables: this configuration must agree with that DDL, and the
/// composite key carries the partition column.
/// </summary>
public class DigestEntryConfiguration : IEntityTypeConfiguration<DigestEntry>
{
    public void Configure(EntityTypeBuilder<DigestEntry> builder)
    {
        builder.ToTable("DigestEntries");

        builder.HasKey(d => new { d.CardiMemberId, d.LocalDate, d.Audience });

        builder.Property(d => d.Audience)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        // Digests are 2–4 sentences by prompt design; the cap is a guard against a runaway
        // generation ever being stored, not a format the text is expected to approach.
        builder.Property(d => d.Text)
            .IsRequired()
            .HasMaxLength(4000);

        builder.Property(d => d.GeneratedAtUtc)
            .IsRequired();
    }
}
