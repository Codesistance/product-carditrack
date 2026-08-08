using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.UnitTests.Infrastructure;

/// <summary>
/// Covers the typed-builder overload. Neither test compiles against the non-generic helper
/// alone — that is the point of them: the design-time factory builds
/// DbContextOptions&lt;CardiTrackDbContext&gt;, which a base-typed return cannot produce.
/// </summary>
public class NpgsqlOptionsExtensionsTests
{
    private const string ConnectionString =
        "Host=localhost;Database=carditrack_optionstest;Username=postgres;Password=postgres";

    /// <summary>
    /// The cast inside the overload is only safe because UseNpgsql hands back the instance it
    /// was given. Asserting it here means an EF change to that contract fails a test rather
    /// than throwing InvalidCastException at every host's startup.
    /// </summary>
    [Fact]
    public void UseCardiTrackNpgsql_OnTypedBuilder_ReturnsTheSameBuilder()
    {
        var builder = new DbContextOptionsBuilder<CardiTrackDbContext>();

        DbContextOptionsBuilder<CardiTrackDbContext> returned =
            builder.UseCardiTrackNpgsql(ConnectionString);

        Assert.Same(builder, returned);
    }

    /// <summary>The shape the design-time factory needs: chain straight through to typed options.</summary>
    [Fact]
    public void UseCardiTrackNpgsql_OnTypedBuilder_ChainsToTypedOptions()
    {
        DbContextOptions<CardiTrackDbContext> options =
            new DbContextOptionsBuilder<CardiTrackDbContext>()
                .UseCardiTrackNpgsql(ConnectionString, b => b.MigrationsAssembly("CardiTrack.Infrastructure"))
                .Options;

        Assert.Equal(typeof(CardiTrackDbContext), options.ContextType);
    }
}
