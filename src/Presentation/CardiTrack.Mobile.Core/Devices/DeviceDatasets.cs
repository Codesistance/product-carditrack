using System.Globalization;

namespace CardiTrack.Mobile.Core.Devices;

/// <summary>
/// Maps the OAuth scopes granted on a device connection to the datasets CardiTrack actually
/// pulls from that device, for the pills on the M1-15 device card.
/// </summary>
/// <remarks>
/// A scope is a permission, not a promise: a bundle earns a name here only where
/// <c>GoogleHealthApiClient</c> actually reads it, so the pills say what the card can show rather
/// than what Google would let us ask for.
/// <para>
/// HRV joined <c>health_metrics_and_measurements</c> in the 2026-08 sweep, so it now gets a pill
/// where the remark below once said it could not: the test has not changed, the client has — it
/// fetches HRV.
/// </para>
/// <para>
/// SpO2, VO2 max, breathing rate and body temperature are named here because
/// <c>GoogleHealthApiClient</c> now ingests all four under <c>health_metrics_and_measurements</c>, which
/// is the same test every other name passes (issue #82). They join the existing <b>Body</b> family
/// rather than introducing one, so under M1-15's one-pill-per-family row the four cost a single
/// pill between them — none at all where <c>Weight</c> already put <b>Body</b> on the card. Note
/// this is the M1-15 row only: the M1-09 Key Metrics cards still show steps, heart rate and sleep
/// alone, because each card needs a hand-authored icon and a Figma slot these four lack.
/// </para>
/// </remarks>
public static class DeviceDatasets
{
    private const string Steps = "Steps";
    private const string Distance = "Distance";
    private const string ActiveMinutes = "Active Minutes";
    private const string Floors = "Floors";
    private const string Calories = "Calories";
    private const string HeartRate = "Heart Rate";
    private const string RestingHeartRate = "Resting HR";
    private const string HeartRateVariability = "HRV";
    private const string Weight = "Weight";
    private const string Spo2 = "SpO2";
    private const string Vo2Max = "VO2 Max";
    private const string BreathingRate = "Breathing Rate";
    private const string Temperature = "Temperature";
    private const string Sleep = "Sleep";
    private const string SleepStages = "Sleep Stages";
    private const string Profile = "Profile";

    /// <summary>
    /// Every dataset we can name, in display order. Pills are sorted by this order so the row
    /// reads the same whatever order the provider returned the scopes in, and so the families
    /// stay grouped. Families run Activity → Heart → Sleep → Body → Other, matching the
    /// "Activity, HR, Sleep" reading order the M1-15 design specifies.
    /// </summary>
    private static readonly DeviceDataset[] Catalogue =
    [
        new(Steps, DatasetFamily.Activity),
        new(Distance, DatasetFamily.Activity),
        new(ActiveMinutes, DatasetFamily.Activity),
        new(Floors, DatasetFamily.Activity),
        new(Calories, DatasetFamily.Activity),
        new(HeartRate, DatasetFamily.Heart),
        new(RestingHeartRate, DatasetFamily.Heart),
        new(HeartRateVariability, DatasetFamily.Heart),
        new(Sleep, DatasetFamily.Sleep),
        new(SleepStages, DatasetFamily.Sleep),
        new(Weight, DatasetFamily.Body),
        new(Spo2, DatasetFamily.Body),
        new(Vo2Max, DatasetFamily.Body),
        new(BreathingRate, DatasetFamily.Body),
        new(Temperature, DatasetFamily.Body),
        new(Profile, DatasetFamily.Other),
    ];

    /// <summary>
    /// Normalised scope → the datasets it unlocks. Keys cover the Google Health API bundles the
    /// Fitbit provider requests today and the legacy Fitbit Web API short names, which are still
    /// stored against connections made before the migration.
    /// </summary>
    private static readonly Dictionary<string, string[]> DatasetsByScope = new(StringComparer.Ordinal)
    {
        // Google Health API (https://www.googleapis.com/auth/googlehealth.<bundle>.readonly)
        ["activity_and_fitness"] = [Steps, Distance, ActiveMinutes, Floors, Calories],
        ["health_metrics_and_measurements"] =
            [HeartRate, RestingHeartRate, HeartRateVariability, Spo2, Vo2Max, BreathingRate,
             Temperature],
        ["sleep"] = [Sleep, SleepStages],

        // Legacy Fitbit Web API
        ["activity"] = [Steps, Distance, ActiveMinutes, Floors, Calories],
        ["heartrate"] = [HeartRate, RestingHeartRate],
        ["weight"] = [Weight],
        ["oxygen_saturation"] = [Spo2],
        ["spo2"] = [Spo2],
        ["profile"] = [Profile],
    };

    /// <summary>Words the humanising fallback must not title-case into nonsense.</summary>
    private static readonly Dictionary<string, string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hr"] = "HR",
        ["hrv"] = "HRV",
        ["ecg"] = "ECG",
        ["spo2"] = "SpO2",
        ["vo2"] = "VO2",
        ["bmi"] = "BMI",
        ["api"] = "API",
    };

    /// <summary>
    /// Returns the datasets the granted <paramref name="scopes"/> unlock, de-duplicated (two
    /// scopes can grant the same reading) and in <see cref="Catalogue"/> order. Scopes we don't
    /// recognise are humanised rather than dropped — a connection sharing something we can't name
    /// is still sharing it — and land after the known ones, in the order they were granted.
    /// </summary>
    public static IReadOnlyList<DeviceDataset> For(IEnumerable<string>? scopes)
    {
        if (scopes is null)
            return [];

        var known = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<DeviceDataset>();
        var seenUnknown = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in scopes)
        {
            var normalised = Normalise(scope);
            if (normalised.Length == 0)
                continue;

            if (DatasetsByScope.TryGetValue(normalised, out var names))
            {
                foreach (var name in names)
                    known.Add(name);
            }
            else
            {
                var humanised = Humanise(normalised);
                if (humanised.Length > 0 && seenUnknown.Add(humanised))
                    unknown.Add(new DeviceDataset(humanised, DatasetFamily.Other));
            }
        }

        return [.. Catalogue.Where(d => known.Contains(d.Name)), .. unknown];
    }

    /// <summary>
    /// The same datasets as <see cref="For"/>, collapsed to one group per family in catalogue
    /// order. This is what the M1-15 card renders: the number of pills a card shows is then
    /// bounded by the number of families (five), not by how generous the grant was, so two cards
    /// stay the same height and comparable at a glance whatever each device shares.
    /// </summary>
    public static IReadOnlyList<DeviceDatasetGroup> GroupedFor(IEnumerable<string>? scopes) =>
    [
        .. For(scopes)
            .GroupBy(d => d.Family)
            .Select(g => new DeviceDatasetGroup(g.Key, [.. g]))
    ];

    /// <summary>
    /// Reduces a granted scope to its bundle name: <c>googlehealth.sleep.readonly</c> and the full
    /// <c>https://www.googleapis.com/auth/googlehealth.sleep.readonly</c> URI both become
    /// <c>sleep</c>, matching the legacy short name for free.
    /// </summary>
    private static string Normalise(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return string.Empty;

        var value = scope.Trim();

        var lastSlash = value.LastIndexOf('/');
        if (lastSlash >= 0)
            value = value[(lastSlash + 1)..];

        value = value.ToLowerInvariant();

        const string vendorPrefix = "googlehealth.";
        if (value.StartsWith(vendorPrefix, StringComparison.Ordinal))
            value = value[vendorPrefix.Length..];

        const string readonlySuffix = ".readonly";
        if (value.EndsWith(readonlySuffix, StringComparison.Ordinal))
            value = value[..^readonlySuffix.Length];

        return value;
    }

    /// <summary>
    /// Turns an unmapped scope into something readable — <c>irregular_rhythm</c> → "Irregular
    /// Rhythm" — so an unrecognised grant never renders as the raw scope string.
    /// </summary>
    private static string Humanise(string normalisedScope)
    {
        var words = normalisedScope.Split(['_', '-', '.', ' '], StringSplitOptions.RemoveEmptyEntries);

        return string.Join(' ', words.Select(word =>
            Acronyms.TryGetValue(word, out var acronym)
                ? acronym
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word)));
    }
}
