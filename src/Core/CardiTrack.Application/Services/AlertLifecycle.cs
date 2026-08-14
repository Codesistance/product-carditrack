using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// The one place <see cref="Alert.AcknowledgedDate"/> and <see cref="Alert.IsResolved"/> are read
/// as a lifecycle position — see <see cref="AlertStatus"/>, which promises that every projection
/// maps them the same way.
/// </summary>
/// <remarks>
/// It used to be promised rather than enforced: the alerts list derived the three states here,
/// while the dashboard's own strip carried a single <c>IsAcknowledged</c> flag and labelled every
/// acknowledged alert "Resolved" — two different readings of the same two columns, on two screens
/// a caregiver moves between.
/// </remarks>
public static class AlertLifecycle
{
    public static AlertStatus StatusOf(Alert alert) =>
        alert.IsResolved ? AlertStatus.Resolved
        : alert.AcknowledgedDate is not null ? AlertStatus.Acknowledged
        : AlertStatus.New;

    /// <summary>The status as it goes on the wire — lowercase, per the REST contract.</summary>
    public static string StatusLabel(Alert alert) => StatusOf(alert).ToString().ToLowerInvariant();
}
