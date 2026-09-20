using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MemberInsightConfiguration : IEntityTypeConfiguration<MemberInsight>
{
    public void Configure(EntityTypeBuilder<MemberInsight> builder)
    {
        builder.ToTable("MemberInsights");

        builder.HasKey(i => i.Id);

        // Two filtered indexes rather than one composite, because the scopes are keyed
        // differently and Postgres counts nulls as distinct: a single unique index over
        // (member, scope, alert) would let a member collect any number of baseline rows, each
        // with a null AlertId, and none of them in conflict.
        //
        // One member-scoped insight per scope…
        builder.HasIndex(i => new { i.CardiMemberId, i.Scope })
            .IsUnique()
            .HasFilter("\"AlertId\" IS NULL");

        // …and exactly one explanation per alert, whoever writes it.
        builder.HasIndex(i => i.AlertId)
            .IsUnique()
            .HasFilter("\"AlertId\" IS NOT NULL");

        // The retention sweep selects by age across every member, so it gets its own index rather
        // than walking the member one.
        builder.HasIndex(i => i.GeneratedAtUtc);

        // And an unfiltered member index, because the two above do not cover erasure. The member
        // one is filtered to the rows with no alert, so the alert-scoped rows — which are the ones
        // that accumulate, one per alert for the life of the history — are reachable only through
        // AlertId. Deleting a member would therefore scan the whole table, and the privacy path is
        // the last one that should degrade as the product is used.
        builder.HasIndex(i => i.CardiMemberId);

        // By name, following MemberAdviseConfiguration.Topic: this column keys which insight
        // answers which question, and a renumbering must not silently re-scope stored rows.
        builder.Property(i => i.Scope)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Generous ceilings, not the prompts' asked-for lengths: the column guards against a
        // runaway value, not style. Taken from InsightLimits, which the writers fit their text to
        // before saving — the numbers have to agree, and a literal here is how they stop agreeing.
        builder.Property(i => i.Summary).IsRequired().HasMaxLength(InsightLimits.Summary);
        builder.Property(i => i.RecommendedAction).HasMaxLength(InsightLimits.RecommendedAction);
        builder.Property(i => i.KeyFindings).HasMaxLength(InsightLimits.KeyFindings);

        builder.Property(i => i.CreatedDate).HasDefaultValueSql("NOW()");
    }
}
