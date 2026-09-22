using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations");

        // Eight characters, unique: it is how a person names this family to us, so two families
        // answering to one code would make the join flow ambiguous in the worst possible place.
        builder.Property(o => o.FamilyId)
            .IsRequired()
            .HasMaxLength(16);

        builder.HasIndex(o => o.FamilyId)
            .IsUnique()
            .HasFilter("\"FamilyId\" <> ''");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(o => o.Type)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(o => o.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(o => o.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(o => o.UpdatedDate);

        // Indexes
        builder.HasIndex(o => o.Type);
        builder.HasIndex(o => o.IsActive);

        // Ignore navigation properties (no FK constraints)
        builder.Ignore(o => o.Users);
        builder.Ignore(o => o.CardiMembers);
        builder.Ignore(o => o.Subscription);
    }
}
