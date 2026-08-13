using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CardiTrack.Infrastructure.Persistence.Configurations;

public class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("Devices");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.DeviceType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.Manufacturer)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(d => d.ModelName)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(d => d.DisplayName)
            .HasMaxLength(150)
            .IsRequired();

        // JSON fields
        builder.Property(d => d.Capabilities)
            .HasMaxLength(2000)
            .HasDefaultValue("{}");

        builder.Property(d => d.ApiEndpoint)
            .HasMaxLength(500);

        builder.Property(d => d.OAuthConfig)
            .HasMaxLength(2000);

        builder.Property(d => d.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(d => d.SortOrder)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(d => d.IconUrl)
            .HasMaxLength(500);

        builder.Property(d => d.CreatedDate)
            .IsRequired()
            .HasDefaultValueSql("NOW()");

        builder.Property(d => d.UpdatedDate);

        // Indexes
        builder.HasIndex(d => d.DeviceType);
        builder.HasIndex(d => d.IsActive);
        builder.HasIndex(d => d.SortOrder);

        // Seed data — one catalog entry per supported DeviceType
        var seedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        builder.HasData(
            new Device
            {
                Id = Guid.Parse("a1b2c3d4-0001-0000-0000-000000000001"),
                DeviceType = DeviceType.Fitbit,
                Manufacturer = "Fitbit / Google",
                ModelName = "All Models",
                DisplayName = "Fitbit",
                Capabilities = """{"hasHeartRate":true,"hasSleep":true,"hasActivity":true,"hasSpO2":true,"hasStress":false}""",
                ApiEndpoint = "https://health.googleapis.com",
                IsActive = true,
                SortOrder = 1,
                CreatedDate = seedDate
            },
            new Device
            {
                Id = Guid.Parse("a1b2c3d4-0002-0000-0000-000000000002"),
                DeviceType = DeviceType.AppleWatch,
                Manufacturer = "Apple",
                ModelName = "All Models",
                DisplayName = "Apple Watch",
                Capabilities = """{"hasHeartRate":true,"hasSleep":true,"hasActivity":true,"hasSpO2":true,"hasECG":true}""",
                ApiEndpoint = null,
                IsActive = false,
                SortOrder = 2,
                CreatedDate = seedDate
            },
            new Device
            {
                Id = Guid.Parse("a1b2c3d4-0003-0000-0000-000000000003"),
                DeviceType = DeviceType.Garmin,
                Manufacturer = "Garmin",
                ModelName = "All Models",
                DisplayName = "Garmin",
                Capabilities = """{"hasHeartRate":true,"hasSleep":true,"hasActivity":true,"hasSpO2":true,"hasStress":true}""",
                ApiEndpoint = "https://healthapi.garmin.com",
                IsActive = false,
                SortOrder = 3,
                CreatedDate = seedDate
            },
            new Device
            {
                Id = Guid.Parse("a1b2c3d4-0004-0000-0000-000000000004"),
                DeviceType = DeviceType.GalaxyWatch,
                Manufacturer = "Samsung",
                ModelName = "Galaxy Watch",
                DisplayName = "Samsung Galaxy Watch",
                Capabilities = """{"hasHeartRate":true,"hasSleep":true,"hasActivity":true,"hasSpO2":true}""",
                ApiEndpoint = "https://api.shealth.samsung.com",
                IsActive = false,
                SortOrder = 4,
                CreatedDate = seedDate
            },
            new Device
            {
                Id = Guid.Parse("a1b2c3d4-0005-0000-0000-000000000005"),
                DeviceType = DeviceType.Withings,
                Manufacturer = "Withings",
                ModelName = "All Models",
                DisplayName = "Withings",
                Capabilities = """{"hasHeartRate":true,"hasSleep":true,"hasActivity":true,"hasSpO2":true,"hasTemperature":true}""",
                ApiEndpoint = "https://wbsapi.withings.net",
                IsActive = false,
                SortOrder = 5,
                CreatedDate = seedDate
            },
            new Device
            {
                Id = Guid.Parse("a1b2c3d4-0006-0000-0000-000000000006"),
                DeviceType = DeviceType.Oura,
                Manufacturer = "Oura",
                ModelName = "Oura Ring",
                DisplayName = "Oura Ring",
                Capabilities = """{"hasHeartRate":true,"hasSleep":true,"hasActivity":true,"hasSpO2":true,"hasTemperature":true,"hasHRV":true}""",
                ApiEndpoint = "https://api.ouraring.com",
                IsActive = false,
                SortOrder = 6,
                CreatedDate = seedDate
            },
            new Device
            {
                Id = Guid.Parse("a1b2c3d4-0007-0000-0000-000000000007"),
                DeviceType = DeviceType.Whoop,
                Manufacturer = "WHOOP",
                ModelName = "WHOOP Strap",
                DisplayName = "WHOOP",
                Capabilities = """{"hasHeartRate":true,"hasSleep":true,"hasActivity":true,"hasHRV":true,"hasStress":true}""",
                ApiEndpoint = "https://api.prod.whoop.com",
                IsActive = false,
                SortOrder = 7,
                CreatedDate = seedDate
            },
            new Device
            {
                Id = Guid.Parse("a1b2c3d4-0008-0000-0000-000000000008"),
                DeviceType = DeviceType.GooglePixelWatch,
                Manufacturer = "Google",
                ModelName = "Pixel Watch",
                DisplayName = "Google Pixel Watch",
                Capabilities = """{"hasHeartRate":true,"hasSleep":true,"hasActivity":true,"hasSpO2":true,"hasECG":true}""",
                ApiEndpoint = "https://health.googleapis.com",
                IsActive = true,
                SortOrder = 8,
                CreatedDate = seedDate
            }
        );
    }
}
