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

        // GeneratedAtUtc is in the key: summaries are recomputed as data lands and every
        // generation is kept, so a day holds a history rather than one overwritable row.
        builder.HasKey(d => new { d.CardiMemberId, d.LocalDate, d.Audience, d.GeneratedAtUtc });

        builder.Property(d => d.Audience)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        // A few words by prompt design. Nullable because entries written before headlines
        // existed have none, and the apps fall back rather than showing a blank title.
        builder.Property(d => d.Headline)
            .HasMaxLength(120);

        // Summaries are 4–6 sentences by prompt design; the cap is a guard against a runaway
        // generation ever being stored, not a format the text is expected to approach.
        builder.Property(d => d.Text)
            .IsRequired()
            .HasMaxLength(4000);

        // A text[] rather than jsonb: this is a short ordered list of plain strings with no shape
        // of its own, which is exactly what a Postgres array is for, and the provider maps it
        // without a converter. Nullable because a generation that produced no usable suggestions
        // stores none — see DigestGenerationService.CleanSuggestions.
        builder.PrimitiveCollection(d => d.Suggestions)
            .ElementType()
            .HasMaxLength(200);

        builder.Property(d => d.GeneratedAtUtc)
            .IsRequired();
    }
}
