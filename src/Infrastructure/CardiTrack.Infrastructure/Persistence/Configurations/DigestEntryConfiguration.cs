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

        // Nullable because a generation that produced no usable suggestion stores none — see
        // DigestGenerationService.CleanSuggestion.
        builder.Property(d => d.Suggestion)
            .HasMaxLength(260);

        // Same reasoning as Audience: a name survives an incident and an enum renumbering.
        // Nullable for the same reason as Suggestion — a generation the model gave nothing
        // parseable for stores none.
        builder.Property(d => d.Urgency)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(d => d.GeneratedAtUtc)
            .IsRequired();

        // One daybook entry per member per day, enforced where it can actually be enforced. The
        // service's "already reviewed?" probe is a fast path two overlapping executions can both
        // pass before either writes; this index is the written-once contract itself. Partial, so
        // the family series keeps its many-generations-per-day history; legal on this partitioned
        // table because LocalDate is the partition key. The insert absorbs the violation with a
        // bare ON CONFLICT DO NOTHING — see DigestRepository.AddAsync.
        builder.HasIndex(d => new { d.CardiMemberId, d.LocalDate })
            .IsUnique()
            .HasFilter("\"Audience\" = 'Daybook'")
            .HasDatabaseName("IX_DigestEntries_OneDaybookPerDay");
    }
}
