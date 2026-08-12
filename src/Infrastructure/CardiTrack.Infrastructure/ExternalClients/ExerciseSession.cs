namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// One logged exercise session for a civil day. <see cref="HasGpsTrack"/> is the environmental-
/// enrichment gate: most Fitbit models have no GPS chip, and even a GPS-equipped device only
/// records a track when the wearer starts a GPS-tracked workout type — most sessions carry none.
/// </summary>
public sealed record ExerciseSession(string SessionId, DateTime StartUtc, DateTime EndUtc, bool HasGpsTrack);

/// <summary>
/// One representative GPS fix for a session — the point the environmental client is called with.
/// Never persisted: callers use it for exactly one outbound lookup and discard it (see
/// docs/technical/data_protection_architecture.md).
/// </summary>
public sealed record ExerciseGpsPoint(double Latitude, double Longitude, DateTime TimestampUtc);
