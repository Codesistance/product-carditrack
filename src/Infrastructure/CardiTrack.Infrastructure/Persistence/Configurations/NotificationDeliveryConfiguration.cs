using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("NotificationDeliveries");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.SourceType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.SourceId).IsRequired();
        builder.Property(d => d.UserId).IsRequired();
        builder.Property(d => d.CardiMemberId);

        builder.Property(d => d.Category)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.Severity)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.Channel)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.State)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.PushDeviceTokenId);

        builder.Property(d => d.DedupKey)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(d => d.CollapseKey).HasMaxLength(128);

        builder.Property(d => d.ExpiresAt).IsRequired();
        builder.Property(d => d.ScheduledFor);

        builder.Property(d => d.Attempts)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(d => d.NextAttemptAt);
        builder.Property(d => d.LastError).HasMaxLength(1024);
        builder.Property(d => d.ProviderMessageId).HasMaxLength(256);

        builder.Property(d => d.SentDate);
        builder.Property(d => d.DeliveredDate);

        builder.Property(d => d.SendTraceParent).HasMaxLength(64);

        builder.Property(d => d.EscalationStage)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(CardiTrack.Domain.Enums.EscalationStage.Initial)
            .HasSentinel(CardiTrack.Domain.Enums.EscalationStage.Initial);

        builder.Property(d => d.EscalatedFrom);

        builder.Property(d => d.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(d => d.UpdatedDate);

        // Cross-producer collapse is evaluated explicitly at send time, not enforced by this
        // index alone — see the entity's remarks — but the row still has to be unique per key.
        builder.HasIndex(d => d.DedupKey).IsUnique();

        // NotificationDispatchWorker's claim query: due rows in state order.
        builder.HasIndex(d => new { d.State, d.NextAttemptAt });

        // SourceId is polymorphic — always paired with SourceType when looked up.
        builder.HasIndex(d => new { d.SourceType, d.SourceId });

        // Erasure sweep filters by member.
        builder.HasIndex(d => d.CardiMemberId);

        // TTL expiry sweep.
        builder.HasIndex(d => new { d.State, d.ExpiresAt });
    }
}
