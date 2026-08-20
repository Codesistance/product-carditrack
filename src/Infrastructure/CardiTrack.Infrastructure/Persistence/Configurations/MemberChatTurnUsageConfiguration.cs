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

        // A future cost read groups by turn.
        builder.HasIndex(u => u.TurnId);
    }
}
