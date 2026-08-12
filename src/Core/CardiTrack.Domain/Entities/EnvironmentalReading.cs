namespace CardiTrack.Domain.Entities;

/// <summary>
/// Derived ambient temperature and air quality for one GPS-tagged exercise session, looked up
/// from the session's location and time. Derived data — regenerable, never authoritative, the
/// same as <see cref="RealtimeAssessment"/>.
/// <para>
/// Composite-keyed (CardiMemberId, SessionStartUtc) and day-partitioned on
/// <see cref="SessionStartUtc"/>, the same shape as <see cref="RealtimeAssessment"/>. There is
/// deliberately no latitude/longitude column anywhere on this entity: the enrichment pass reads
/// the session's coordinates only long enough to call the environmental client and discards them
/// immediately after — nothing past that call ever carries a coordinate into storage.
/// </para>
/// </summary>
public class EnvironmentalReading
{
    public Guid CardiMemberId { get; set; }

    /// <summary>Start of the exercise session (UTC). The partition key.</summary>
    public DateTime SessionStartUtc { get; set; }

    public DateTime SessionEndUtc { get; set; }

    /// <summary>The connection the session was fetched from — audit only, not a join target.</summary>
    public Guid DeviceConnectionId { get; set; }

    public double? TemperatureCelsius { get; set; }

    public int? AirQualityIndex { get; set; }

    public string? AirQualityCategory { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
}
