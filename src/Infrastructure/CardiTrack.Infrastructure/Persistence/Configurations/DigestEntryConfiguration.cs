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
        // Both books' written-once contracts are partial unique indexes over the same two columns,
        // separated only by their filter. They must be declared through the *named* HasIndex
        // overload: the unnamed one is keyed on the property set, so a second call over the same
        // properties reconfigures the first rather than adding another — which EF then scaffolds
        // as "drop the Daybook index, create the Weekbook one", silently taking the Daybook's
        // guarantee away. The model name is the second argument; HasDatabaseName is what the
        // database sees.
        builder.HasIndex(d => new { d.CardiMemberId, d.LocalDate }, "IX_DigestEntries_OneDaybookPerDay")
            .IsUnique()
            .HasFilter("\"Audience\" = 'Daybook'")
            .HasDatabaseName("IX_DigestEntries_OneDaybookPerDay");

        // A member has a Daybook and a Weekbook on the same LocalDate most weeks — the Weekbook is
        // dated by the last day of the week it covers, which is also a day with its own Daybook —
        // so one index over both audiences would refuse the second write. Filtered per audience,
        // each series is unique within itself and indifferent to the other.
        //
        // Uniqueness is on (member, date) alone because LocalDate already identifies the period:
        // one week ends on any given date, so "one Weekbook per member per week" and "one per
        // member per end-date" are the same statement.
        builder.HasIndex(d => new { d.CardiMemberId, d.LocalDate }, "IX_DigestEntries_OneWeekbookPerWeek")
            .IsUnique()
            .HasFilter("\"Audience\" = 'Weekbook'")
            .HasDatabaseName("IX_DigestEntries_OneWeekbookPerWeek");

        // And the Monthbook's, on the same terms: its date is the month's last day, which is also
        // a day with a Daybook and — when the month ends on the week's last day — a Weekbook too.
        builder.HasIndex(d => new { d.CardiMemberId, d.LocalDate }, "IX_DigestEntries_OneMonthbookPerMonth")
            .IsUnique()
            .HasFilter("\"Audience\" = 'Monthbook'")
            .HasDatabaseName("IX_DigestEntries_OneMonthbookPerMonth");
    }
}
