using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class MedicalEntryConfiguration : IEntityTypeConfiguration<MedicalEntry>
{
    public void Configure(EntityTypeBuilder<MedicalEntry> builder)
    {
        builder.ToTable("MedicalEntries");

        builder.HasKey(e => e.Id);

        // Unbounded: the plaintext is capped by the validator, but ciphertext is longer than what
        // it encrypts — the same reasoning CardiMemberConfiguration records for MedicalNotes.
        builder.Property(e => e.Text)
            .IsRequired();

        // By name, so a query result reads "Allergy" during an incident and survives a renumbering.
        builder.Property(e => e.Kind)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(e => e.AddedAtUtc)
            .IsRequired();

        builder.Property(e => e.CreatedDate)
            .HasDefaultValueSql("NOW()");

        // No foreign key to CardiMembers, like MemberQuestionnaires: nothing cascades, so erasure
        // deletes these rows by name (MemberErasureService) and its report counts them.

        // Every read is one member's whole ledger.
        builder.HasIndex(e => e.CardiMemberId);

        // Account erasure nulls these by user, across every member.
        builder.HasIndex(e => e.AddedByUserId);
        builder.HasIndex(e => e.RemovedByUserId);
    }
}
