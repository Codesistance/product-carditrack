namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Provider-neutral rhythm events for one member for one civil day — the ECG readings the wearer
/// took and the irregular-rhythm notifications their device raised unprompted.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="DeviceHealthSnapshot"/> rather than folded into it because these two
/// data types are the only ones CardiTrack reads that sit behind their own OAuth scopes
/// (<c>googlehealth.ecg.readonly</c> and <c>googlehealth.irn.readonly</c>). A connection made
/// before those scopes shipped cannot serve them, and the caller checks the connection's granted
/// scopes before spending a request — the same gate <c>CaptureBatteryAsync</c> applies to the
/// settings scope. Folding them into the snapshot would have made every wearer pay two guaranteed
/// 403s per pull to learn something the stored scope list already knows.
/// <para>
/// Every count is null when the day could not be read at all and a number — zero included — when it
/// could. That distinction is the whole point: "no notification was raised today" is reassuring,
/// "we cannot see notifications for this wearer" is not, and a caregiver's summary must never
/// present the second as the first.
/// </para>
/// </remarks>
public sealed record DeviceRhythmDay(
    int? EcgReadings,
    int? EcgAtrialFibrillationReadings,
    int? IrregularRhythmNotifications)
{
    /// <summary>Nothing read — the shared instance for a connection without the rhythm scopes.</summary>
    public static readonly DeviceRhythmDay None = new(null, null, null);

    /// <summary>True when any count was readable, whatever its value.</summary>
    public bool HasAnyData =>
        EcgReadings is not null
        || EcgAtrialFibrillationReadings is not null
        || IrregularRhythmNotifications is not null;
}
