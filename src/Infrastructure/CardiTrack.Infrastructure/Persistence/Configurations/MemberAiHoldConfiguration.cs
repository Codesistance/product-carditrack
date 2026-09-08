using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MemberAiHoldConfiguration : IEntityTypeConfiguration<MemberAiHold>
{
    public void Configure(EntityTypeBuilder<MemberAiHold> builder)
    {
        builder.ToTable("MemberAiHolds");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.CardiMemberId).IsRequired();

        // Enums persist as names throughout this schema; a human triaging a held member reads
        // the purpose straight off the row.
        builder.Property(h => h.Purpose).IsRequired().HasConversion<string>().HasMaxLength(40);

        builder.Property(h => h.HeldUntilUtc).IsRequired();
        builder.Property(h => h.LastFailedAtUtc).IsRequired();
        builder.Property(h => h.ConsecutiveFailures).IsRequired();
        builder.Property(h => h.Reason).IsRequired().HasMaxLength(40);

        builder.Property(h => h.CreatedDate).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(h => h.UpdatedDate);

        // One hold per member per purpose: the read is a point lookup on this pair, and the
        // upsert's conflict target is this index.
        builder.HasIndex(h => new { h.CardiMemberId, h.Purpose }).IsUnique();
    }
}
