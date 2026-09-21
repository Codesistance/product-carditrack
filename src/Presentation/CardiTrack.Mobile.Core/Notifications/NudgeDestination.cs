namespace CardiTrack.Mobile.Core.Notifications;

/// <summary>Where a notification's action link points, in the app's own terms.</summary>
public enum NudgeDestinationKind
{
    /// <summary>The link named nothing this version of the app can open.</summary>
    Unknown = 0,

    MemberDetail,
    MemberEdit,

    /// <summary>
    /// This member's health background — <c>carditrack://cardimembers/{id}/edit#medicalNotes</c>,
    /// from the two medical-notes rules. Its own kind rather than <see cref="MemberEdit"/> because
    /// the fragment names a different intent: the caregiver came to write or confirm one thing,
    /// and the profile form is a page of fields they can disturb on the way to it.
    /// </summary>
    MemberMedicalNotes,
    MemberDevices,
    MemberBaseline,

    /// <summary>A push-originated question — <c>carditrack://cardimembers/{memberId}/questions</c>,
    /// from FcmNotificationChannel's Questionnaire deep link.</summary>
    MemberQuestions,
    Settings,

    /// <summary>A push-originated alert (Safety/Health) — <c>carditrack://alerts/{alertId}</c>, from FcmNotificationChannel's content-free payload.</summary>
    AlertDetail,

    /// <summary>A push-originated nudge — <c>carditrack://notifications/{notificationId}</c>, for the two safety-class nudge rules that do push.</summary>
    NotificationDetail,

    /// <summary>
    /// The time zone, which the app answers in place from the device's own clock rather than by
    /// navigating anywhere.
    /// </summary>
    TimeZone
}

/// <summary>A parsed action link: what to open, and for whom.</summary>
/// <param name="EntityId">
/// The alert or notification id for <see cref="NudgeDestinationKind.AlertDetail"/>/
/// <see cref="NudgeDestinationKind.NotificationDetail"/>. Alert taps land on the detail
/// screen (M1-11/12/16); notification taps still go to the inbox until that screen exists.
/// </param>
public readonly record struct NudgeDestination(NudgeDestinationKind Kind, Guid? CardiMemberId, Guid? EntityId = null)
{
    public static readonly NudgeDestination Unknown = new(NudgeDestinationKind.Unknown, null);
}

/// <summary>
/// Parses the <c>carditrack://</c> action links a notification carries.
/// </summary>
/// <remarks>
/// <para>
/// Parsing lives here, in the testable half, while the mapping from a destination to an actual
/// Shell route stays in the MAUI project. The server names a destination in product terms — "this
/// member's devices" — and the app decides which screen that is today, so renaming a page is a
/// client change rather than a migration over stored notifications.
/// </para>
/// <para>
/// An unrecognised link parses to <see cref="NudgeDestinationKind.Unknown"/> rather than a guess.
/// A comply button that navigates somewhere arbitrary is worse than one that admits the screen
/// does not exist yet.
/// </para>
/// </remarks>
public static class NudgeLinkParser
{
    private const string Scheme = "carditrack://";

    public static NudgeDestination Parse(string? deepLink)
    {
        if (string.IsNullOrWhiteSpace(deepLink) || !deepLink.StartsWith(Scheme, StringComparison.Ordinal))
            return NudgeDestination.Unknown;

        // Fragments address a field within a screen ("#medicalNotes"). They are read for intent
        // and never passed on: a fragment is this parser's business, and a route that received one
        // would not know what to do with it. Where a fragment names a destination of its own, it
        // resolves to that destination below rather than to the path's.
        var withoutScheme = deepLink[Scheme.Length..];
        var hashIndex = withoutScheme.IndexOf('#');
        var fragment = hashIndex >= 0 ? withoutScheme[(hashIndex + 1)..] : null;
        var path = (hashIndex >= 0 ? withoutScheme[..hashIndex] : withoutScheme).Trim('/');

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return NudgeDestination.Unknown;

        return segments switch
        {
            ["settings", ..] when string.Equals(fragment, "timezone", StringComparison.OrdinalIgnoreCase)
                => new(NudgeDestinationKind.TimeZone, null),

            ["settings", ..] => new(NudgeDestinationKind.Settings, null),

            ["cardimembers", var id, "devices", ..] when Guid.TryParse(id, out var forDevices)
                => new(NudgeDestinationKind.MemberDevices, forDevices),

            ["cardimembers", var id, "questions", ..] when Guid.TryParse(id, out var forQuestions)
                => new(NudgeDestinationKind.MemberQuestions, forQuestions),

            // Before the bare edit case: the fragment is the more specific intent, and a caregiver
            // sent to the top of the profile form to write one note has been sent to the wrong
            // place — which is what happened to both medical-notes rules until this existed.
            ["cardimembers", var id, "edit"]
                when string.Equals(fragment, "medicalNotes", StringComparison.OrdinalIgnoreCase)
                     && Guid.TryParse(id, out var forNotes)
                => new(NudgeDestinationKind.MemberMedicalNotes, forNotes),

            ["cardimembers", var id, "edit"] when Guid.TryParse(id, out var forEdit)
                => new(NudgeDestinationKind.MemberEdit, forEdit),

            ["cardimembers", var id, "baseline"] when Guid.TryParse(id, out var forBaseline)
                => new(NudgeDestinationKind.MemberBaseline, forBaseline),

            ["cardimembers", var id] when Guid.TryParse(id, out var forDetail)
                => new(NudgeDestinationKind.MemberDetail, forDetail),

            ["alerts", var id] when Guid.TryParse(id, out var alertId)
                => new(NudgeDestinationKind.AlertDetail, null, alertId),

            ["notifications", var id] when Guid.TryParse(id, out var notificationId)
                => new(NudgeDestinationKind.NotificationDetail, null, notificationId),

            _ => NudgeDestination.Unknown
        };
    }
}
