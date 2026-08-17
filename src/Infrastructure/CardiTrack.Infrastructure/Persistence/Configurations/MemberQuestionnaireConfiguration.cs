using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MemberQuestionnaireConfiguration : IEntityTypeConfiguration<MemberQuestionnaire>
{
    public void Configure(EntityTypeBuilder<MemberQuestionnaire> builder)
    {
        builder.ToTable("MemberQuestionnaires");

        builder.HasKey(q => q.Id);

        // Deliberately unbounded, both of them. The plaintext is capped in the service, but
        // ciphertext is longer than what it encrypts and the envelope adds a fingerprint on top, so
        // a varchar sized to the plaintext would reject values the service considers valid — the
        // same reasoning CardiMemberConfiguration records for MedicalNotes.
        builder.Property(q => q.QuestionText)
            .IsRequired();

        builder.Property(q => q.AnswerText);

        builder.Property(q => q.TriggerContext)
            .HasMaxLength(500);

        // Stored as its name: a status column that reads "Pending" in a query result is worth more
        // during an incident than one that reads 1, and it survives someone renumbering the enum.
        builder.Property(q => q.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(q => q.GeneratedAtUtc)
            .IsRequired();

        // Same reasoning as Status: a name survives an incident and an enum renumbering.
        builder.Property(q => q.Scope)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(q => q.CreatedDate)
            .HasDefaultValueSql("NOW()");

        // The pending probe runs for every member the digest job considers, every pass.
        builder.HasIndex(q => new { q.CardiMemberId, q.Status });

        // The expiry sweep asks the opposite question of the probe above: not "is this member
        // holding one" but "which rows anywhere have lapsed". Status leads, because pending is a
        // small and shrinking slice of the table while AskableUntilUtc is set on almost every row.
        builder.HasIndex(q => new { q.Status, q.AskableUntilUtc });

        // Serves both the interval probe and the newest-first list the apps read.
        builder.HasIndex(q => new { q.CardiMemberId, q.GeneratedAtUtc });
    }
}
