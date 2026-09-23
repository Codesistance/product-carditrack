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

        // SHA-256 as lowercase hex, so exactly 64 characters, fixed-width.
        builder.Property(j => j.FindingFingerprint)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(j => j.JudgedAtUtc).IsRequired();

        builder.Property(j => j.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(j => j.UpdatedDate);

        // The whole point of the table: one judgement per member, per rule, per local day, per set
        // of figures. The fingerprint is what makes it safe — readings that move make a different
        // key and are judged again — and the day is still in the index so a question that does not
        // name its own date cannot carry a verdict into tomorrow. Unique rather than merely
        // indexed because two overlapping assessor executions can both judge the same finding —
        // the pass has no claim row, deliberately — and the second insert losing to a constraint
        // is the correct outcome, not an error worth surfacing.
        builder.HasIndex(j => new { j.CardiMemberId, j.Rule, j.LocalDate, j.FindingFingerprint })
            .IsUnique();

        // For the retention sweep, which reads by age and nothing else.
        builder.HasIndex(j => j.JudgedAtUtc);
    }
}
