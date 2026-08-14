using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.OrganizationId).IsRequired();
        builder.Property(n => n.UserId).IsRequired();
        builder.Property(n => n.CardiMemberId);

        builder.Property(n => n.RuleCode)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(n => n.RuleVersion)
            .IsRequired()
            .HasDefaultValue(1);

        builder.Property(n => n.Category)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(n => n.Priority)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(n => n.Fingerprint)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(n => n.TitleKey).IsRequired().HasMaxLength(128);
        builder.Property(n => n.BodyKey).IsRequired().HasMaxLength(128);
        builder.Property(n => n.BenefitKey).IsRequired().HasMaxLength(128);

        // Counters only — never a name or a metric value. See the entity's remarks.
        builder.Property(n => n.TemplateData)
            .IsRequired()
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb");

        builder.Property(n => n.ActionDeepLink)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(n => n.State)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(n => n.SnoozedUntil);
        builder.Property(n => n.ResolvedDate);

        builder.Property(n => n.ResolutionReason)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(n => n.FirstDetectedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(n => n.LastEvaluatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(n => n.FirstSeenDate);
        builder.Property(n => n.PushedDate);

        builder.Property(n => n.IsOwner)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(n => n.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(n => n.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(n => n.UpdatedDate);

        // The reconciler's identity. Unique so a concurrent second run loses the insert rather
        // than producing a duplicate the user would see twice.
        builder.HasIndex(n => n.Fingerprint).IsUnique();

        // The inbox query: one user's visible rows, ranked.
        builder.HasIndex(n => new { n.UserId, n.State, n.Priority });

        // Reconciliation loads the stored set for a user, and the erasure sweep filters by member.
        builder.HasIndex(n => new { n.UserId, n.RuleCode });
        builder.HasIndex(n => n.CardiMemberId);
        builder.HasIndex(n => n.OrganizationId);

        // Retention purges terminal rows by age.
        builder.HasIndex(n => new { n.State, n.ResolvedDate });

        // The dispatch worker's push sweep, every 30 seconds. The filter carries the sweep's whole
        // predicate rather than PushedDate alone: PushedDate is null for every row of every
        // non-pushable rule and for every terminal one, so filtering on it by itself would index
        // most of the table. With the state predicates the index holds only rows the sweep can act
        // on — open, owned, live, unpushed — which is a transient population no matter how large
        // Notifications grows. RuleCode is the key because it is the one predicate left for the
        // index to discriminate on.
        builder.HasIndex(n => n.RuleCode)
            .HasFilter("\"PushedDate\" IS NULL AND \"IsActive\" AND \"IsOwner\" AND \"State\" = 'Open'")
            .HasDatabaseName("IX_Notifications_PendingPush");
    }
}
