using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MemberChatSessionConfiguration : IEntityTypeConfiguration<MemberChatSession>
{
    public void Configure(EntityTypeBuilder<MemberChatSession> builder)
    {
        builder.ToTable("MemberChatSessions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.StartedAtUtc)
            .IsRequired();

        builder.Property(s => s.LastTurnAtUtc)
            .IsRequired();

        builder.Property(s => s.CreatedDate)
            .HasDefaultValueSql("NOW()");

        // "Does this caregiver already have an active session for this member" — asked on every
        // message send, before a new session is created.
        builder.HasIndex(s => new { s.CardiMemberId, s.UserId, s.LastTurnAtUtc });

        builder.HasMany(s => s.Turns)
            .WithOne()
            .HasForeignKey(t => t.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
