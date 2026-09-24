namespace CardiTrack.Mobile.Core.Diagnostics;

/// <summary>What to do with a stored session-telemetry choice when a caregiver signs in.</summary>
public enum TelemetryChoiceAction
{
    /// <summary>The stored choice is this caregiver's (or there is none): leave it.</summary>
    Keep,

    /// <summary>A choice saved before owners were recorded: treat it as this caregiver's and record them.</summary>
    Adopt,

    /// <summary>Somebody else's choice: forget it, so this caregiver starts from the default.</summary>
    Forget,
}

/// <summary>
/// Whose "off" a stored telemetry choice is. Sign-out forgets the choice, but a session that
/// expires does not come through sign-out, so without an owner the next person to sign in on the
/// phone would inherit the last one's setting. The owner is the same one-way per-caregiver token
/// the telemetry notice uses (<see cref="TelemetryNotice.SeenValueFor"/>).
/// </summary>
public static class TelemetryChoiceOwner
{
    /// <param name="hasChoice">Whether a choice is stored at all.</param>
    /// <param name="storedOwner">The owner recorded with it, or null for one saved before owners were.</param>
    /// <param name="email">The caregiver now signing in.</param>
    public static TelemetryChoiceAction OnSignIn(bool hasChoice, string? storedOwner, string? email)
    {
        if (!hasChoice)
            return TelemetryChoiceAction.Keep;

        var current = TelemetryNotice.SeenValueFor(email);

        // No identity to compare with: keep what is stored rather than risk dropping an objection.
        if (current is null)
            return TelemetryChoiceAction.Keep;

        // Saved before owners were recorded. Sign-out has always removed the choice, so one left
        // behind belongs to whoever was signed in when the app updated — almost always the same
        // caregiver silently signing back in. Adopting it honours an earlier "off".
        if (string.IsNullOrEmpty(storedOwner))
            return TelemetryChoiceAction.Adopt;

        return string.Equals(storedOwner, current, StringComparison.Ordinal)
            ? TelemetryChoiceAction.Keep
            : TelemetryChoiceAction.Forget;
    }
}
