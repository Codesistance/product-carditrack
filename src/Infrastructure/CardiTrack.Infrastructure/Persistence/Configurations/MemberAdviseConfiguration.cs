using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MemberAdviseConfiguration : IEntityTypeConfiguration<MemberAdvise>
{
    public void Configure(EntityTypeBuilder<MemberAdvise> builder)
    {
        builder.ToTable("MemberAdvises");

        builder.HasKey(a => a.Id);

        // One suggestion per member, and the dashboard's read is by member — the unique index is
        // both the upsert guarantee and the lookup path.
        builder.HasIndex(a => a.CardiMemberId).IsUnique();

        // Generous ceilings, not the prompt's asked-for lengths: the writer already caps what the
        // model returns, and the column guards against a runaway value, not style.
        builder.Property(a => a.Summary).IsRequired().HasMaxLength(500);
        builder.Property(a => a.Suggestion).IsRequired().HasMaxLength(500);
        builder.Property(a => a.GuidelineCited).HasMaxLength(200);

        builder.Property(a => a.CreatedDate).HasDefaultValueSql("NOW()");
    }
}
