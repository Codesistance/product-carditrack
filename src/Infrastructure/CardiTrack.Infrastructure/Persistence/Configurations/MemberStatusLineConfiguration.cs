using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MemberStatusLineConfiguration : IEntityTypeConfiguration<MemberStatusLine>
{
    public void Configure(EntityTypeBuilder<MemberStatusLine> builder)
    {
        builder.ToTable("MemberStatusLines");

        builder.HasKey(s => s.Id);

        // One line per member, and the dashboard's read is by member — the unique index is both
        // the upsert guarantee and the lookup path.
        builder.HasIndex(s => s.CardiMemberId).IsUnique();

        // Generous ceilings, not the prompt's asked-for lengths: the writer already cleans and
        // caps what the model returns, and the column guards against a runaway value, not style.
        builder.Property(s => s.Headline).HasMaxLength(80);
        builder.Property(s => s.Message).IsRequired().HasMaxLength(500);

        builder.Property(s => s.CreatedDate).HasDefaultValueSql("NOW()");
    }
}
