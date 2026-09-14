using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class CardiMemberCreationKeyConfiguration : IEntityTypeConfiguration<CardiMemberCreationKey>
{
    public void Configure(EntityTypeBuilder<CardiMemberCreationKey> builder)
    {
        builder.ToTable("CardiMemberCreationKeys");

        builder.HasKey(k => k.Id);

        builder.Property(k => k.Key)
            .HasMaxLength(64)
            .IsRequired();

        // The unique index is the guarantee, not the pre-read in the service. Two attempts under
        // one key that arrive close enough together to both pass that read will both try to insert,
        // and exactly one succeeds — which is the whole point of a key. The loser is told to retry,
        // and its retry finds the winner's member.
        builder.HasIndex(k => new { k.UserId, k.Key }).IsUnique();

        // What the opportunistic purge scans.
        builder.HasIndex(k => new { k.UserId, k.CreatedDate });
    }
}
