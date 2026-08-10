using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class NotificationRunLogConfiguration : IEntityTypeConfiguration<NotificationRunLog>
{
    public void Configure(EntityTypeBuilder<NotificationRunLog> builder)
    {
        builder.ToTable("NotificationRunLogs");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.StartedAt)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(r => r.CompletedAt);
        builder.Property(r => r.LastOrganizationId);

        builder.Property(r => r.OrganizationsScanned).IsRequired().HasDefaultValue(0);
        builder.Property(r => r.Created).IsRequired().HasDefaultValue(0);
        builder.Property(r => r.Resolved).IsRequired().HasDefaultValue(0);
        builder.Property(r => r.Suppressed).IsRequired().HasDefaultValue(0);
        builder.Property(r => r.DurationMs).IsRequired().HasDefaultValue(0);

        builder.Property(r => r.Error).HasMaxLength(2000);

        builder.Property(r => r.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(r => r.UpdatedDate);

        builder.HasIndex(r => r.StartedAt);
    }
}
