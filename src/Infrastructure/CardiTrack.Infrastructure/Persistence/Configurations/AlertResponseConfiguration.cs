using CardiTrack.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class AlertResponseConfiguration : IEntityTypeConfiguration<AlertResponse>
{
    public void Configure(EntityTypeBuilder<AlertResponse> builder)
    {
        builder.ToTable("AlertResponses");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.AlertId).IsRequired();
        builder.Property(r => r.UserId).IsRequired();

        builder.Property(r => r.Kind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        // The catalogue's code, not its label. Short by construction; bounded so a client cannot
        // store an essay in the field the server validates against a fixed list.
        builder.Property(r => r.ResponseCode).HasMaxLength(64);

        // Encrypted at rest by AlertService, the same treatment CardiMember.MedicalNotes gets.
        // Deliberately unbounded for the same reason: the request caps the note at 500
        // characters, but base64 of AES-GCM ciphertext over 500 multi-byte characters runs several
        // times longer, and a 500-char column would reject input the API accepted.
        builder.Property(r => r.Note);

        builder.Property(r => r.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        // The whole read pattern: every response on one alert, newest first. Nothing looks a
        // response up by its own id.
        builder.HasIndex(r => new { r.AlertId, r.CreatedDate });
    }
}
