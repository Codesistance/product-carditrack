namespace CardiTrack.Domain.Common;

/// <summary>
/// How far a reading has to sit from the member's own usual before a CardiJournal book names a
/// direction for it, and the bounds a caregiver may move that to. One source of truth for the
/// prompt builder, the API validator and the app, the same stance
/// <see cref="JournalSchedule"/> takes on timing.
/// </summary>
/// <remarks>
/// <para>
/// These exist because a book states a direction — "went to bed later than usual", "0.8h above
/// it" — and a direction is a claim. Below some distance the claim is noise the reading cannot
/// support: a wearable's sleep-onset detection is itself accurate to minutes, and the usual it is
/// measured against is a thirty-day circular mean, so "3m later than usual" is arithmetic dressed
/// as a finding. Past some other distance the claim stops being decidable at all — see
/// <see cref="DefaultDirectionBoundMinutes"/>.
/// </para>
/// <para>
/// Held per member rather than as constants because the distance that matters is not the same for
/// everyone: a member whose nights are metronomic makes twenty minutes meaningful, and one whose
/// bedtime wanders by an hour makes it noise. Null everywhere means the defaults below, so a
/// member nobody has tuned reads exactly as they did before the setting existed.
/// </para>
/// </remarks>
public static class JournalComparison
{
    /// <summary>Minutes in a day — the circle every clock comparison is made on.</summary>
    public const int MinutesPerDay = 1440;

    /// <summary>
    /// Half the clock. The furthest two times of day can be from each other: past this the
    /// shortest way round the circle turns back, so no signed difference can exceed it.
    /// </summary>
    public const int HalfDayMinutes = MinutesPerDay / 2;

    /// <summary>
    /// How far a bedtime must move before the book calls it earlier or later. Wider than
    /// <see cref="DefaultWakeToleranceMinutes"/> because bedtime is the looser of the two in this
    /// cohort — an evening runs long, a programme finishes late — while waking is pulled to a
    /// routine.
    /// </summary>
    public const int DefaultBedtimeToleranceMinutes = 20;

    /// <inheritdoc cref="DefaultBedtimeToleranceMinutes"/>
    public const int DefaultWakeToleranceMinutes = 10;

    /// <summary>
    /// Past this distance the book stops naming a direction and says only that the time was far
    /// off the usual.
    /// </summary>
    /// <remarks>
    /// A clock comparison is made on a circle, so the further apart two times are the less
    /// "earlier" and "later" mean: at <see cref="HalfDayMinutes"/> they are the same statement.
    /// A bedtime eleven hours "earlier" than usual is not an early night, it is an afternoon sleep
    /// filed as one — and a book that called it early would be confidently wrong about the one
    /// reading a family would query. Six hours is where the direction stops being decidable in
    /// practice: further than any real night moves, closer than the point the arithmetic itself
    /// gives up.
    /// </remarks>
    public const int DefaultDirectionBoundMinutes = 360;

    /// <summary>
    /// A band, as a percentage of the member's own usual, inside which a <b>numeric</b> reading is
    /// called level with it rather than above or below.
    /// </summary>
    /// <remarks>
    /// Zero by default, and zero is not "off": every numeric clause already refuses to name a
    /// direction for a difference its own format would print as nothing, which is a floor no
    /// setting can lower — a difference that renders as "0h" must never be reported as "0h above
    /// it". This widens that floor for a caregiver who finds one-percent movements noisy; it can
    /// never narrow it.
    /// </remarks>
    public const decimal DefaultLevelTolerancePercent = 0m;

    /// <summary>
    /// The widest tolerance a caregiver may set. Two hours of bedtime drift called "about their
    /// usual" is a setting that has stopped describing a routine.
    /// </summary>
    public const int MaximumToleranceMinutes = 120;

    /// <summary>
    /// The narrowest and widest direction bound. Never past <see cref="HalfDayMinutes"/>, which no
    /// difference can exceed — a bound above it could never be reached and would read as a
    /// setting that does nothing.
    /// </summary>
    public const int MinimumDirectionBoundMinutes = 60;

    /// <inheritdoc cref="MinimumDirectionBoundMinutes"/>
    public const int MaximumDirectionBoundMinutes = HalfDayMinutes;

    /// <summary>The widest level band a caregiver may set.</summary>
    public const decimal MaximumLevelTolerancePercent = 25m;

    /// <summary>
    /// The granularity a chosen level band must land on. The column stores one decimal place, so
    /// a finer value is not a finer setting — it is one the database rounds on the way in and
    /// hands back as a number the caregiver never chose.
    /// </summary>
    /// <remarks>
    /// The same stance <see cref="JournalSchedule.StepMinutes"/> takes, and for the same reason
    /// its validator gives: a value is refused rather than rounded, because silently moving 2.55
    /// to 2.6 saves a setting nobody picked and then shows it back to them as though they had.
    /// </remarks>
    public const int LevelTolerancePercentDecimals = 1;

    /// <summary>
    /// The clock tolerances a client offers, in minutes. A ladder rather than every minute in the
    /// range: the values a caregiver is actually choosing between are a quarter of an hour apart,
    /// and a control offering 121 of them asks them to pick a number rather than a judgement.
    /// </summary>
    /// <remarks>
    /// Offerable, not enforceable — unlike <see cref="JournalSchedule.StepMinutes"/>, which is a
    /// real capability limit because the generator only runs on the half hour and could not honour
    /// 02:17. Thirty-seven minutes is a perfectly honourable tolerance; it is simply not one worth
    /// a rung on a picker. So the ladder rides in the response to keep the app from inventing its
    /// own, and validation stays the range — a value off the ladder is unusual, not invalid.
    /// </remarks>
    public static IReadOnlyList<int> SelectableToleranceMinutes { get; } =
        Array.AsReadOnly(new[] { 0, 5, 10, 15, 20, 30, 45, 60, 90, 120 });

    /// <summary>
    /// The direction bounds a client offers, in minutes. Coarser than the tolerances above,
    /// because this one is answering "how far round the clock before the question stops making
    /// sense" — an hour either way does not change that answer.
    /// </summary>
    /// <inheritdoc cref="SelectableToleranceMinutes" path="/remarks"/>
    public static IReadOnlyList<int> SelectableDirectionBoundMinutes { get; } =
        Array.AsReadOnly(new[] { 60, 90, 120, 180, 240, 360, 480, 720 });

    /// <summary>
    /// The level bands a client offers, as percentages of the member's own usual. Four rungs
    /// rather than a range, because this is the setting a caregiver has the least vocabulary for:
    /// a client is expected to show these in plain words — none, slight, moderate, large — and
    /// keep the percentage behind them.
    /// </summary>
    /// <inheritdoc cref="SelectableToleranceMinutes" path="/remarks"/>
    public static IReadOnlyList<decimal> SelectableLevelTolerancePercents { get; } =
        Array.AsReadOnly(new[] { 0m, 1m, 2m, 5m });

    /// <summary>Whether a chosen clock tolerance is one a book can honour. Null is always valid.</summary>
    public static bool IsSelectableTolerance(int? minutes) =>
        minutes is null || (minutes >= 0 && minutes <= MaximumToleranceMinutes);

    /// <summary>Whether a chosen direction bound is one a clock comparison can reach.</summary>
    public static bool IsSelectableDirectionBound(int? minutes) =>
        minutes is null
        || (minutes >= MinimumDirectionBoundMinutes && minutes <= MaximumDirectionBoundMinutes);

    /// <summary>
    /// Whether a chosen level band is inside the range a reading stays readable across, and lands
    /// on a value the column can actually hold. Null is always valid.
    /// </summary>
    public static bool IsSelectableLevelTolerance(decimal? percent)
    {
        if (percent is not { } value)
            return true;

        if (value < 0m || value > MaximumLevelTolerancePercent)
            return false;

        return decimal.Round(value, LevelTolerancePercentDecimals) == value;
    }

    /// <summary>The stored tolerances, each defaulted where the caregiver has chosen nothing.</summary>
    public static JournalComparisonTolerances Effective(
        int? bedtimeToleranceMinutes,
        int? wakeToleranceMinutes,
        int? directionBoundMinutes,
        decimal? levelTolerancePercent) =>
        new(
            bedtimeToleranceMinutes ?? DefaultBedtimeToleranceMinutes,
            wakeToleranceMinutes ?? DefaultWakeToleranceMinutes,
            directionBoundMinutes ?? DefaultDirectionBoundMinutes,
            levelTolerancePercent ?? DefaultLevelTolerancePercent);

    /// <summary>The defaults, for a caller with no member to hand — fixtures and prompt tests.</summary>
    public static JournalComparisonTolerances Defaults { get; } = Effective(null, null, null, null);
}

/// <summary>
/// The four distances a book's comparison clauses are judged against, already defaulted. A value
/// type carried into the prompt builders so a generator cannot forget one and silently fall back
/// to a constant that disagrees with the member's setting.
/// </summary>
/// <param name="BedtimeToleranceMinutes">
/// Inside this, a bedtime reads as about their usual rather than earlier or later.
/// </param>
/// <param name="WakeToleranceMinutes"><inheritdoc cref="BedtimeToleranceMinutes"/></param>
/// <param name="DirectionBoundMinutes">
/// At or past this, no direction is named at all — see
/// <see cref="JournalComparison.DefaultDirectionBoundMinutes"/>.
/// </param>
/// <param name="LevelTolerancePercent">
/// A band around a numeric usual, as a percentage of it, inside which the reading is called level.
/// </param>
public readonly record struct JournalComparisonTolerances(
    int BedtimeToleranceMinutes,
    int WakeToleranceMinutes,
    int DirectionBoundMinutes,
    decimal LevelTolerancePercent);
