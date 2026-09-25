using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Common;

namespace CardiTrack.Mobile.Core.Members;

/// <summary>
/// The one answer to "what does the app call this member?" — their first name, everywhere it
/// greets or labels them. Full names are for exports and the server's clinical contexts, not for
/// the app's copy.
/// </summary>
/// <remarks>
/// Reads the server's stored first name. Falls back to the first word of the full name only when
/// the first name is missing, which is what an API from before the first/last split sends: the
/// app and the API ship separately, so for a while a new build can be talking to an old server,
/// and it should still say "Margaret" rather than nothing.
/// </remarks>
public static class MemberNames
{
    /// <summary>The stored first name, or the first word of <paramref name="fullName"/> when there is none.</summary>
    public static string FirstName(string? firstName, string? fullName) =>
        !string.IsNullOrWhiteSpace(firstName)
            ? firstName.Trim()
            : PersonName.Split(fullName).FirstName;

    public static string DisplayFirstName(this CardiMemberResponse member) =>
        FirstName(member.FirstName, member.Name);

    public static string DisplayFirstName(this CardiMemberDetailResponse member) =>
        FirstName(member.FirstName, member.Name);

    /// <summary>
    /// The last name to put in the edit form: the stored one, or — from an API that predates the
    /// split — the rest of the full name, so saving an untouched form restates what was there.
    /// </summary>
    public static string? DisplayLastName(this CardiMemberDetailResponse member) =>
        string.IsNullOrWhiteSpace(member.FirstName) ? PersonName.Split(member.Name).LastName : member.LastName;

    public static string DisplayFirstName(this DashboardResponse dashboard) =>
        FirstName(dashboard.FirstName, dashboard.Name);

    public static string MemberFirstName(this AlertSummaryResponse alert) =>
        FirstName(alert.CardiMemberFirstName, alert.CardiMemberName);

    public static string MemberFirstName(this AlertDetailResponse alert) =>
        FirstName(alert.CardiMemberFirstName, alert.CardiMemberName);

    /// <summary>Null when the notification is not about a member, so callers keep their own "your family member" wording.</summary>
    public static string? MemberFirstName(this NotificationResponse notification) =>
        NullIfEmpty(FirstName(notification.CardiMemberFirstName, notification.CardiMemberName));

    /// <inheritdoc cref="MemberFirstName(NotificationResponse)"/>
    public static string? MemberFirstName(this NotificationMuteResponse mute) =>
        NullIfEmpty(FirstName(mute.CardiMemberFirstName, mute.CardiMemberName));

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
