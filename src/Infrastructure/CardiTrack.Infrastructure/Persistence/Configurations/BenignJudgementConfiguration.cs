using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class BenignJudgementConfiguration : IEntityTypeConfiguration<BenignJudgement>
{
    public void Configure(EntityTypeBuilder<BenignJudgement> builder)
    {
        builder.ToTable("BenignJudgements");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.CardiMemberId).IsRequired();

        builder.Property(j => j.Rule)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(j => j.LocalDate).IsRequired();

        builder.Property(j => j.JudgedAtUtc).IsRequired();

        builder.Property(j => j.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(j => j.UpdatedDate);

        // The whole point of the table: one judgement per member, per rule, per local day. Unique
        // rather than merely indexed because two overlapping assessor executions can both judge
        // the same finding — the pass has no claim row, deliberately — and the second insert
        // losing to a constraint is the correct outcome, not an error worth surfacing.
        builder.HasIndex(j => new { j.CardiMemberId, j.Rule, j.LocalDate }).IsUnique();

        // For the retention sweep, which reads by age and nothing else.
        builder.HasIndex(j => j.JudgedAtUtc);
    }
}
