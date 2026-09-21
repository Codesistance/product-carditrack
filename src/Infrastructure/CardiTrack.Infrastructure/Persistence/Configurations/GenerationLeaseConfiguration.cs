using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class GenerationLeaseConfiguration : IEntityTypeConfiguration<GenerationLease>
{
    public void Configure(EntityTypeBuilder<GenerationLease> builder)
    {
        builder.ToTable("GenerationLeases");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.CardiMemberId).IsRequired();

        // By name, following MemberAiHoldConfiguration.Purpose: a human reading a held row should
        // see which writer holds it, and a renumbering must not re-point a lease at another.
        builder.Property(l => l.Work).IsRequired().HasConversion<string>().HasMaxLength(40);

        builder.Property(l => l.PeriodEnd).IsRequired();
        builder.Property(l => l.HeldUntilUtc).IsRequired();
        builder.Property(l => l.ClaimedAtUtc).IsRequired();

        builder.Property(l => l.CreatedDate).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(l => l.UpdatedDate);

        // One lease per member per work, which is both the claim's guarantee and this upsert's
        // conflict target. Keyed without the period on purpose — see GenerationLease's remarks:
        // it bounds the table at members x works, so nothing has to sweep it.
        builder.HasIndex(l => new { l.CardiMemberId, l.Work }).IsUnique();
    }
}
