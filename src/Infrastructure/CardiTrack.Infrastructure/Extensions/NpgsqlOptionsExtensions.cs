using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace CardiTrack.Infrastructure.Extensions;

public static class NpgsqlOptionsExtensions
{
    /// <summary>
    /// The single spelling of UseNpgsql for every CardiTrack host. Callers pass their own
    /// provider tweaks (migrations assembly and the like) through <paramref name="configure"/>;
    /// everything shared lives here.
    /// <para>
    /// It exists as one helper rather than an inline call per host because ConfigureDataSource
    /// is untracked: EF resolves one NpgsqlDataSource internally and disregards differing
    /// configuration across calls, so two registrations of the same context that disagree do
    /// not fail — one silently wins. The API registers both AddDbContext and
    /// AddDbContextFactory for CardiTrackDbContext, which is exactly that shape.
    /// </para>
    /// </summary>
    public static DbContextOptionsBuilder UseCardiTrackNpgsql(
        this DbContextOptionsBuilder options,
        string? connectionString,
        Action<NpgsqlDbContextOptionsBuilder>? configure = null)
        => options.UseNpgsql(connectionString, npgsql =>
        {
            // Npgsql traces physical connection opens by default, one CONNECT span apiece.
            // They are pure overhead on the trace bill: a connect is not a unit of work anyone
            // reads a trace to understand, and at dev's 1.0 head sampling every one ships.
            // Command spans are untouched — the query detail is the reason DB tracing is on.
            // Connection health stays visible through Npgsql's meter (see ApmExtensions),
            // which is where pool behaviour belongs anyway.
            npgsql.ConfigureDataSource(source =>
                source.ConfigureTracing(tracing => tracing.EnablePhysicalOpenTracing(false)));

            configure?.Invoke(npgsql);
        });
}
