using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class DeviceConnectionInviteConfiguration : IEntityTypeConfiguration<DeviceConnectionInvite>
{
    /// <summary>
    /// The statuses an invite can still be completed from. Spelled out as the stored names because
    /// the partial index filter below is raw SQL: EF cannot notice a rename of
    /// <see cref="DeviceInviteStatus"/> here, so a rename must change this too or the one-live-invite
    /// guarantee silently stops applying.
    /// </summary>
    private const string LiveStatusFilter = "\"Status\" IN ('Pending', 'Opened')";

    /// <summary>Lower-case hex of a SHA-256 — always exactly this many characters.</summary>
    private const int TokenHashLength = 64;

    public void Configure(EntityTypeBuilder<DeviceConnectionInvite> builder)
    {
        builder.ToTable("DeviceConnectionInvites");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.CardiMemberId).IsRequired();
        builder.Property(i => i.CreatedByUserId).IsRequired();

        // Enums persist as names throughout this schema; someone reading a stuck invite sees
        // "Opened" and "QrCode", not 2 and 2.
        builder.Property(i => i.DeviceType).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(i => i.Channel).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.Status).IsRequired().HasConversion<string>().HasMaxLength(20);

        builder.Property(i => i.TokenHash)
            .IsRequired()
            .HasMaxLength(TokenHashLength)
            .IsFixedLength();

        builder.Property(i => i.ExpiresAt).IsRequired();
        builder.Property(i => i.OpenedAt);
        builder.Property(i => i.ResolvedAt);
        builder.Property(i => i.DeviceConnectionId);

        builder.Property(i => i.CreatedDate).IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(i => i.UpdatedDate);

        // The wearer's every request arrives as a token and nothing else, so this lookup is the
        // whole hot path for that half of the feature. Unique because two invites sharing a hash
        // would mean two sharing a token, which for 256 bits of randomness means a bug in minting
        // rather than a collision — and this is where such a bug surfaces immediately instead of
        // handing somebody another member's invitation.
        builder.HasIndex(i => i.TokenHash)
            .IsUnique()
            .HasDatabaseName("IX_DeviceConnectionInvites_TokenHash");

        // At most one live invite per member and brand. The service supersedes the old one before
        // inserting, but a caregiver double-tapping — or two caregivers in the same care circle
        // tapping at once — is a race the check cannot close. The insert that loses surfaces as a
        // conflict rather than leaving the wearer with two live links to the same consent.
        builder.HasIndex(i => new { i.CardiMemberId, i.DeviceType })
            .IsUnique()
            .HasFilter(LiveStatusFilter)
            .HasDatabaseName("IX_DeviceConnectionInvites_OneLivePerMemberDevice");

        // The Worker's retention sweep reads by how long ago an invite stopped mattering, which for
        // a finished one is when it resolved and for an abandoned one is when it expired.
        builder.HasIndex(i => i.ExpiresAt)
            .HasDatabaseName("IX_DeviceConnectionInvites_ExpiresAt");
    }
}
