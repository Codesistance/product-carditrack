using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class UserCardiMemberConfiguration : IEntityTypeConfiguration<UserCardiMember>
{
    public void Configure(EntityTypeBuilder<UserCardiMember> builder)
    {
        builder.ToTable("UserCardiMembers");

        builder.HasKey(uc => uc.Id);

        builder.Property(uc => uc.UserId)
            .IsRequired();

        builder.Property(uc => uc.CardiMemberId)
            .IsRequired();

        builder.Property(uc => uc.RelationshipType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(uc => uc.IsPrimaryCaregiver)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(uc => uc.CanViewHealthData)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(uc => uc.ReceiveAlerts)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(uc => uc.AssignedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(uc => uc.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(uc => uc.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(uc => uc.UpdatedDate);

        // Indexes
        builder.HasIndex(uc => uc.UserId);
        builder.HasIndex(uc => uc.CardiMemberId);
        builder.HasIndex(uc => new { uc.UserId, uc.CardiMemberId, uc.IsActive });
        builder.HasIndex(uc => uc.IsPrimaryCaregiver);
    }
}
