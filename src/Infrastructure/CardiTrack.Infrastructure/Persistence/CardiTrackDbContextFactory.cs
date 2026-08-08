using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace CardiTrack.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for creating DbContext during migrations
/// </summary>
public class CardiTrackDbContextFactory : IDesignTimeDbContextFactory<CardiTrackDbContext>
{
    public CardiTrackDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        var loader = new ConfigurationLoader(configuration);
        // Fall back to a dummy value so design-time commands (migrations, has-pending-model-changes)
        // can instantiate the DbContext without a real database connection.
        var connectionString = loader.Get(ConfigurationKeys.ConnectionStrings.DefaultConnection)
            ?? "Host=localhost;Database=carditrack_designtime;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<CardiTrackDbContext>();
        optionsBuilder.UseCardiTrackNpgsql(connectionString, b => b.MigrationsAssembly("CardiTrack.Infrastructure"));

        return new CardiTrackDbContext(optionsBuilder.Options);
    }
}
