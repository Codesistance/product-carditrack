using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> builder)
    {
        builder.ToTable("Reports");

        builder.HasKey(r => r.Id);

        // Every read of a report is "this user's report with this id" — the id alone is never
        // enough (see Report.OwnerUserId). Leading with the owner also serves the account-scoped
        // listing an erasure or DSAR sweep needs.
        builder.HasIndex(r => new { r.OwnerUserId, r.Id });

        // The cleanup worker's sweep: expired rows, whatever their status.
        builder.HasIndex(r => r.ExpiresAt);

        // Same convention as every other enum in the schema — stored as its name, so a migration
        // that reorders the enum can't silently reinterpret existing rows.
        builder.Property(r => r.Format)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // uuid[] rather than a join table: the set is small, fixed at queue time, and never
        // queried by member — it exists to render the export and to answer "what did this cover".
        builder.Property(r => r.CardiMemberIds)
            .HasColumnType("uuid[]")
            .IsRequired();

        builder.Property(r => r.Title).HasMaxLength(200);

        // Bucket object names are bounded by our own naming (reports/{guid}/{guid:N}.{ext}).
        builder.Property(r => r.ObjectName).HasMaxLength(200);
        builder.Property(r => r.ContentType).HasMaxLength(100);
        builder.Property(r => r.FileName).HasMaxLength(200);

        // Generic caregiver-facing copy only; diagnostics go to the logs.
        builder.Property(r => r.Error).HasMaxLength(500);

        builder.Property(r => r.CreatedDate).HasDefaultValueSql("NOW()");
    }
}
