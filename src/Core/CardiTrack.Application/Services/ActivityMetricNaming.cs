namespace CardiTrack.Application.Services;

/// <summary>
/// What the app calls <c>ActivityLog.ActiveMinutes</c>, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Five caregiver-facing surfaces name this metric — the insight card, the trend narrative, the
/// doctor-visit report, and the week and month books — and until this existed each carried its own
/// literal. Only two of the five were held in step, by
/// <c>ItCoversTheSameMetricsTheTrendFeaturesDo</c>; the other three drifted silently, which is how
/// three of them were still saying "Active minutes" after the other two had been renamed.
/// </para>
/// <para>
/// <b>The name is not decoration.</b> The provider sums MODERATE and VIGOROUS only and drops
/// LIGHT — deliberately, so the figure matches the one a wearer sees in their own app
/// (<c>GoogleHealthApiClient.ActiveActivityLevels</c>). Someone who walks a long way at a gentle
/// pace therefore scores almost none of it. Under the old name, "active minutes", a caregiver
/// read a small number as somebody who had barely moved, when it belonged to somebody who had
/// been walking all afternoon. Anything renaming this should rename it here.
/// </para>
/// </remarks>
public static class ActivityMetricNaming
{
    /// <summary>The label, wherever the metric is shown or described.</summary>
    public const string Label = "Harder activity";

    /// <summary>
    /// What qualifies the minutes, for a surface to append to its own unit. Kept apart from the
    /// unit itself because the surfaces phrase units differently — the report writes "a day"
    /// where the card writes "minutes a day" — and only the qualifier has to agree.
    /// </summary>
    public const string PaceQualifier = "at moderate pace or above";

    /// <summary>"minutes a day at moderate pace or above", for the surfaces that spell the unit out.</summary>
    public const string MinutesPerDayUnit = "minutes a day " + PaceQualifier;

    /// <summary>"a day at moderate pace or above", for the surfaces that leave the noun to the column.</summary>
    public const string PerDayUnit = "a day " + PaceQualifier;
}
