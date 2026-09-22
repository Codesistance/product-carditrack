using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class FamilyJoinRequestConfiguration : IEntityTypeConfiguration<FamilyJoinRequest>
{
    public void Configure(EntityTypeBuilder<FamilyJoinRequest> builder)
    {
        builder.ToTable("FamilyJoinRequests");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.OrganizationId).IsRequired();
        builder.Property(r => r.RequestedByUserId).IsRequired();

        builder.Property(r => r.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(r => r.ExpiresAt).IsRequired();
        builder.Property(r => r.ResolvedByUserId);
        builder.Property(r => r.ResolvedAt);

        builder.Property(r => r.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(r => r.UpdatedDate);

        // The admin's queue, and the asker's own list.
        builder.HasIndex(r => new { r.OrganizationId, r.Status });
        builder.HasIndex(r => r.RequestedByUserId);

        // One live request per person per family. Asking twice is a double tap, not a second
        // request, and without this an asker could fill an admin's queue by retrying.
        builder.HasIndex(r => new { r.RequestedByUserId, r.OrganizationId })
            .IsUnique()
            .HasFilter("\"Status\" = 'Pending'");
    }
}
