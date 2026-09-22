using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the rhythm-episode table — day-partitioned parent created by raw SQL in the migration, the
/// same arrangement as <see cref="RealtimeAssessmentConfiguration"/>: this configuration only has
/// to agree with that DDL, and the composite key carries the partition column.
/// </summary>
public class RhythmEpisodeConfiguration : IEntityTypeConfiguration<RhythmEpisode>
{
    public void Configure(EntityTypeBuilder<RhythmEpisode> builder)
    {
        builder.ToTable("RhythmEpisodes");

        // The partition key must be part of the primary key — PostgreSQL enforces it — and so must
        // the connection: these rows are kept per device, exactly like GranularMetricHours. Two
        // watches on one wearer can raise notifications over the same minutes, and each window is
        // a measurement one of them made; keying on (member, window) alone would let whichever
        // device synced second overwrite the other's reading and quietly halve the evidence.
        builder.HasKey(e => new { e.CardiMemberId, e.DeviceConnectionId, e.WindowStartUtc });

        builder.Property(e => e.WindowEndUtc).IsRequired();
        builder.Property(e => e.NotificationStartUtc).IsRequired();
        builder.Property(e => e.Positive).IsRequired();
        builder.Property(e => e.BeatCount).IsRequired();

        // integer[] rather than a child table: these are read whole or not at all — an episode's
        // beats have no meaning apart from the episode — and a row per beat would turn one
        // notification into a few thousand rows carrying the same foreign key.
        builder.Property(e => e.RrMilliseconds)
            .IsRequired()
            .HasColumnType("integer[]");

        builder.Property(e => e.OffsetMillisFromStart)
            .IsRequired()
            .HasColumnType("integer[]");

        builder.Property(e => e.MeanRrMs).IsRequired();
        builder.Property(e => e.MinRrMs).IsRequired();
        builder.Property(e => e.MaxRrMs).IsRequired();
        builder.Property(e => e.IngestedAtUtc).IsRequired();

        // The range read (GetInRangeAsync). The primary key cannot serve it: DeviceConnectionId
        // sits between the member and the window, so a (member, window-range) scan cannot use the
        // key's ordering and would fall back to scanning each partition.
        builder.HasIndex(e => new { e.CardiMemberId, e.WindowStartUtc });

        // Grouping a notification's windows back into the one thing the wearer was told about.
        builder.HasIndex(e => new { e.CardiMemberId, e.NotificationStartUtc });
    }
}
