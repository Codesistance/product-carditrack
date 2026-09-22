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

        // And a family has exactly one admin. IFamilyWriteGuard is what actually keeps that true
        // — every path that moves the role takes FOR UPDATE on the family first, so two transfers
        // queue instead of racing. This index is the backstop underneath it: the invariant the
        // roster, the approval queue and "the admin pays" all rest on should not be enforceable
        // only by remembering to take a lock. A future path that forgets fails loudly here rather
        // than quietly leaving a family with two admins or none.
        // Named explicitly, because it is a second index on the same column as the roster lookup
        // above and EF would otherwise treat this as redefining that one — dropping the plain
        // index every "who is in this family" query uses.
        builder.HasIndex(uo => new { uo.OrganizationId, uo.Role, uo.IsActive })
            .IsUnique()
            .HasFilter("\"Role\" = 'Admin' AND \"IsActive\"")
            .HasDatabaseName("IX_UserOrganizations_OneActiveAdmin");
    }
}
