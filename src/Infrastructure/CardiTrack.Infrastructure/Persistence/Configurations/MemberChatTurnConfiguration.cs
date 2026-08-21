using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MemberChatTurnConfiguration : IEntityTypeConfiguration<MemberChatTurn>
{
    public void Configure(EntityTypeBuilder<MemberChatTurn> builder)
    {
        builder.ToTable("MemberChatTurns");

        builder.HasKey(t => t.Id);

        // Deliberately unbounded — same reasoning as MemberQuestionnaireConfiguration.QuestionText:
        // ciphertext is longer than the plaintext it encrypts, and the plaintext is capped in the
        // service, not here.
        builder.Property(t => t.Content)
            .IsRequired();

        // Unbounded and optional, same ciphertext reasoning as Content above; null whenever the
        // turn had no charts to keep.
        builder.Property(t => t.Charts);

        // A name survives an incident and an enum renumbering — same reasoning as
        // MemberQuestionnaireConfiguration.Status.
        builder.Property(t => t.Role)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(t => t.CreatedAtUtc)
            .IsRequired();

        builder.Property(t => t.CreatedDate)
            .HasDefaultValueSql("NOW()");

        // Reading one session's history back, oldest first, is the only access pattern this table
        // serves today.
        builder.HasIndex(t => new { t.SessionId, t.CreatedAtUtc });
    }
}
