using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MemberChatTurnUsageConfiguration : IEntityTypeConfiguration<MemberChatTurnUsage>
{
    public void Configure(EntityTypeBuilder<MemberChatTurnUsage> builder)
    {
        builder.ToTable("MemberChatTurnUsages");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Step)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(u => u.ProviderSlot)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(u => u.ModelName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(u => u.CreatedDate)
            .HasDefaultValueSql("NOW()");

        // A real FK with cascade, not just an index: deleting a session cascades to its turns
        // (MemberChatSessionConfiguration), and the erase has to reach the token ledger too —
        // an orphaned usage row would outlive the conversation it accounts for, undermining the
        // Art. 17 hard-delete guarantee the session's remarks promise.
        builder.HasOne<MemberChatTurn>()
            .WithMany()
            .HasForeignKey(u => u.TurnId)
            .OnDelete(DeleteBehavior.Cascade);

        // A future cost read groups by turn. (The FK above also creates an index on TurnId in
        // PostgreSQL via EF's FK convention, but stating it keeps the read intent explicit.)
        builder.HasIndex(u => u.TurnId);
    }
}
