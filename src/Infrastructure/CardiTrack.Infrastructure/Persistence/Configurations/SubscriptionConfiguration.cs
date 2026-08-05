using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("Subscriptions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.OrganizationId)
            .IsRequired();

        builder.Property(s => s.Tier)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(s => s.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(s => s.StartDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(s => s.EndDate);

        builder.Property(s => s.TrialEndDate);

        builder.Property(s => s.BillingCycle)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(s => s.Price)
            .IsRequired()
            .HasColumnType("decimal(10,2)");

        builder.Property(s => s.Currency)
            .HasMaxLength(3)
            .IsRequired()
            .HasDefaultValue("USD");

        builder.Property(s => s.MaxCardiMembers)
            .IsRequired();

        builder.Property(s => s.MaxUsers)
            .IsRequired();

        // JSON fields
        builder.Property(s => s.Features)
            .HasMaxLength(2000)
            .HasDefaultValue("{}");

        builder.Property(s => s.PaymentMethod)
            .HasMaxLength(1000);

        builder.Property(s => s.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(s => s.UpdatedDate);

        // Indexes
        builder.HasIndex(s => s.OrganizationId).IsUnique();
        builder.HasIndex(s => s.Status);
        builder.HasIndex(s => new { s.Status, s.EndDate });

        // FK-only relationship (no navigations — Organization's are deliberately
        // ignored). Cascade delete guarantees a subscription can never outlive its
        // organization, so orphan cleanup is a single organization delete.
        builder.HasOne<Organization>()
            .WithOne()
            .HasForeignKey<Subscription>(s => s.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
