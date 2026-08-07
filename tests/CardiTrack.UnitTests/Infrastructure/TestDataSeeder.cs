using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Infrastructure;

/// <summary>Seeds prerequisite FK records via repositories — no direct DbContext usage.</summary>
public static class TestDataSeeder
{
    public static async Task<Organization> SeedOrganizationAsync(IServiceScope scope)
    {
        var repo = scope.ServiceProvider.GetRequiredService<IOrganizationRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var org = new Organization
        {
            Name = $"Org {Guid.NewGuid():N}",
            Type = OrganizationType.Family,
            IsActive = true
        };

        await repo.AddAsync(org);
        await uow.SaveChangesAsync();
        return org;
    }

    public static async Task<CardiMember> SeedCardiMemberAsync(
        IServiceScope scope,
        Guid organizationId,
        bool isActive = true,
        DateTime? monitoringPausedUntil = null)
    {
        var repo = scope.ServiceProvider.GetRequiredService<ICardiMemberRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var member = new CardiMember
        {
            OrganizationId = organizationId,
            Name = $"Member {Guid.NewGuid():N}",
            DateOfBirth = new DateOnly(1955, 6, 15),
            Gender = Gender.Other,
            MonitoringPausedUntil = monitoringPausedUntil,
            IsActive = isActive
        };

        await repo.AddAsync(member);
        await uow.SaveChangesAsync();
        return member;
    }

    public static async Task<DeviceConnection> SeedDeviceConnectionAsync(
        IServiceScope scope,
        Guid cardiMemberId,
        ConnectionStatus status = ConnectionStatus.Connected,
        bool isActive = true,
        DateTime? lastSyncDate = null,
        int syncFrequencyMinutes = 30)
    {
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var connection = new DeviceConnection
        {
            CardiMemberId = cardiMemberId,
            DeviceType = DeviceType.Fitbit,
            DeviceName = "Test Fitbit",
            ConnectionStatus = status,
            IsActive = isActive,
            AccessToken = "enc_access",
            RefreshToken = "enc_refresh",
            TokenExpiry = DateTime.UtcNow.AddHours(8),
            LastSyncDate = lastSyncDate,
            SyncFrequencyMinutes = syncFrequencyMinutes
        };

        await repo.AddAsync(connection);
        await uow.SaveChangesAsync();
        return connection;
    }
}
