using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MemberAdviseObservationConfiguration : IEntityTypeConfiguration<MemberAdviseObservation>
{
    public void Configure(EntityTypeBuilder<MemberAdviseObservation> builder)
    {
        builder.ToTable("MemberAdviseObservations");

        builder.HasKey(o => o.Id);

        // Every read is "this member's entries, newest first, over a window" — the member and the
        // date are the whole access pattern. Deliberately not unique: this is a log, and the same
        // topic recurring months apart is the point of keeping it.
        builder.HasIndex(o => new { o.CardiMemberId, o.ObservedAtUtc });

        // The retention sweep selects purely on age, across all members, so it gets its own index
        // rather than scanning the composite above — a cutoff query that degrades as the log grows
        // is the one query that must not.
        builder.HasIndex(o => o.ObservedAtUtc);

        // By name, matching MemberAdviseConfiguration: a renumbering of the enum must not silently
        // retopic rows that are meant to read the same years later.
        builder.Property(o => o.Topic)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // The same ceilings the current-guidance row carries, for the same reason: the writer
        // already caps what the model returns, and the column guards a runaway value, not style.
        builder.Property(o => o.Summary).IsRequired().HasMaxLength(500);
        builder.Property(o => o.Suggestion).IsRequired().HasMaxLength(500);
        builder.Property(o => o.GuidelineCited).HasMaxLength(200);

        builder.Property(o => o.ObservedAtUtc).IsRequired();

        builder.Property(o => o.CreatedDate).HasDefaultValueSql("NOW()");
    }
}
