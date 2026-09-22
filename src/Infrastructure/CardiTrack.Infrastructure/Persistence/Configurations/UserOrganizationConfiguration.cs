using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class UserOrganizationConfiguration : IEntityTypeConfiguration<UserOrganization>
{
    public void Configure(EntityTypeBuilder<UserOrganization> builder)
    {
        builder.ToTable("UserOrganizations");

        builder.HasKey(uo => uo.Id);

        builder.Property(uo => uo.UserId)
            .IsRequired();

        builder.Property(uo => uo.OrganizationId)
            .IsRequired();

        // Stored by name like User.Role, so a row reads without the enum to hand.
        builder.Property(uo => uo.Role)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(uo => uo.JoinedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(uo => uo.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(uo => uo.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(uo => uo.UpdatedDate);

        // Indexes
        builder.HasIndex(uo => uo.UserId);
        builder.HasIndex(uo => uo.OrganizationId);

        // A person is in a family once. Re-joining after leaving reactivates the row rather than
        // adding a second, so the history of the membership stays one line.
        builder.HasIndex(uo => new { uo.UserId, uo.OrganizationId })
            .IsUnique();
    }
}
