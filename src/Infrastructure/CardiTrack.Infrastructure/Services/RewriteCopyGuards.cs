using System.Globalization;
using System.Text.RegularExpressions;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The checks every piece of Rewrite-slot copy is held to before it is stored — the code half of
/// two boundaries the rewrite briefs state and cannot enforce: say nothing about the person you
/// were not told, and name no reading the clinical read did not.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the digest, Advise and the status line rather than living in any one of them,
/// because those briefs are the same shape — a clinical read in, family-facing copy out, on a
/// slot that is shown no member context — and a guard on one of them that the others lack is a
/// gap nobody meant to leave.
/// <see cref="AdviseRegisterGuards"/> and <see cref="JournalRegisterGuards"/> stay where they are:
/// those guard a register, which differs per surface, while these two guard what the copy claims,
/// which does not.
/// </para>
/// <para>
/// Both take the same stance the guards around them take — a prompt is a request, not a guarantee
/// — and both are deliberately absent from the prompts themselves, because a negative list in a
/// brief is a list a small model echoes.
/// </para>
/// </remarks>
internal static partial class RewriteCopyGuards
{
    /// <summary>
    /// True when the copy states a sex the member's record does not bear out — the wrong one, or
    /// any one at all for a member whose sex is not on file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run against the model's reply, before <see cref="MemberVoice.Resolve"/> — afterwards "his"
    /// is exactly what the copy is supposed to say, put there by code that looked it up.
    /// </para>
    /// <para>
    /// Not "any sexed pronoun at all", though the Rewrite slot is told none and so invents every
    /// one it writes. A guess that matches the record is a sentence a family can read without
    /// being told anything untrue, and rejecting it would cost that member their summary for the
    /// day over a word that was right. What is rejected is the guess that is wrong, and the guess
    /// that nothing can confirm — <c>PreferNotToSay</c>, where every member created before M1-04
    /// asked for sex still sits, and where a coin toss about someone's father or mother would
    /// otherwise reach the family as fact.
    /// </para>
    /// <para>
    /// "They", "them" and "their" are deliberately not here. They state nothing untrue about the
    /// person, they are what <see cref="MedicalPromptBlocks.PronounsByToken"/> now asks to be
    /// written as tokens, and they are also the ordinary words for the family the copy is
    /// addressed to — so rejecting them would throw away sound copy over a register slip, and
    /// sometimes over a correct sentence.
    /// </para>
    /// </remarks>
    internal static bool StatesAnUnsupportedSex(string? text, Gender gender)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var male = MalePronouns().IsMatch(text);
        var female = FemalePronouns().IsMatch(text);

        if (!male && !female)
            return false;

        return gender switch
        {
            Gender.Male => female,
            Gender.Female => male,
            _ => true,
        };
    }

    /// <summary>
    /// The first reading the copy names that the clinical read did not, or null when the copy
    /// stays within what it was given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this answers, from a summary card on 2026-09-11: a clinical read that said
    /// breathing was slightly higher than usual and never mentioned oxygen at all came back as
    /// "his breathing and oxygen levels remained stable". Oxygen was not softened or misread — it
    /// was not there. A family reading that card was told a reading had been taken and was fine.
    /// </para>
    /// <para>
    /// Only families named in <see cref="ReadingFamilies"/> can be caught, and only the invention
    /// of one: copy that contradicts a reading the read did name — "relatively short" written up
    /// as "a long period of sitting still", from the same card — matches the same family and
    /// passes here. That is a real limit, not an oversight. A polarity check would need to know
    /// which way each reading was moving and which way the sentence about it points, which is the
    /// clinical read's own job, and guessing at it in a regex would discard sound copy far more
    /// often than it caught a flip.
    /// </para>
    /// <para>
    /// Summaries only, never suggestions. A suggestion is an action, and the brief lets it reach
    /// for an already-known routine fact; an afternoon walk proposed against a read about sleep is
    /// the suggestion working as asked, not an invented reading. The summary is the half that
    /// claims what was measured.
    /// </para>
    /// </remarks>
    internal static string? NamesAReadingTheReadDidNot(string? copy, string? read)
    {
        if (string.IsNullOrWhiteSpace(copy) || string.IsNullOrWhiteSpace(read))
            return null;

        var copyText = SeparateVariability(copy);
        var readText = SeparateVariability(read);

        foreach (var (family, words) in ReadingFamilies)
        {
            if (Mentions(copyText, words) && !Mentions(readText, words))
                return family;
        }

        return null;
    }

    /// <summary>
    /// The first sleep duration the copy states that nothing in the data supports, as written, or
    /// null when every one it states is within reach of a figure the data holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this answers, from member chat on 2026-09-25: asked "how did he sleep this
    /// week?", the reply said about 2h 22m a night "compared to his usual average of nearly 6
    /// hours", under a chart of the same fetch whose nights ran 4h 18m to 7h. The usual was right
    /// and the average was not a figure the data could produce. Nothing checked: the family guard
    /// above saw sleep in the read and sleep in the reply and passed it, and the answer check
    /// judges whether a question was answered, never whether its figures are true — it is not
    /// shown the readings (DPIA A20).
    /// </para>
    /// <para>
    /// So a stated duration has to land near one the data holds — within a quarter hour, or a
    /// tenth of the figure for a long one, so "nearly 6 hours" against a 5h 42m usual still passes
    /// — and near one of the right kind, read from the words beside it:
    /// </para>
    /// <list type="bullet">
    /// <item>A <b>difference</b> ("55 minutes less", "down by 40 minutes") is held to the distances
    /// between the figures below. Kept apart from lengths because the two overlap: that week's
    /// average sat about 2h 13m under the 7-hour floor, and one pooled list would have waved the
    /// 2h 22m average through as a difference nobody stated.</item>
    /// <item>A <b>reference</b> ("his usual 5h 45m", "the recommended 7 hours", "at least 7
    /// hours") is held to the member's usual and the published band's edges.</item>
    /// <item>An <b>average</b> ("averaged 4h 48m", "4h 48m a night") is held to the window's
    /// computed averages alone, and there are none when too few nights arrived — the summary
    /// refused to give one, so a thin week's "average" fails even when it happens to sit on the
    /// usual or a band edge.</item>
    /// <item>Any other <b>length</b> is held to all of them: the nights, the averages, the usual
    /// and the band edges.</item>
    /// </list>
    /// <para>
    /// Only sentences about sleep are read, so an hour of activity or a minute count elsewhere in
    /// the reply is never mistaken for a night; and nothing longer than a plausible night is read
    /// as one, so "the last 24 hours" is not a sleep figure.
    /// </para>
    /// <para>
    /// A backstop, not the fix. The window's arithmetic is done in code and handed to the clinical
    /// read (<c>ChatWindowSummaryBlock</c>); this catches the reply that ignores it.
    /// </para>
    /// </remarks>
    /// <param name="supported">
    /// The sleep figures the reply could honestly state — see <see cref="SupportedSleepFigures"/>.
    /// </param>
    internal static string? StatesASleepFigureTheDataDoesNot(string? copy, SleepFigures supported)
    {
        if (string.IsNullOrWhiteSpace(copy))
            return null;

        foreach (var sentence in Sentences().Split(copy))
        {
            if (!Mentions(sentence, SleepSentenceWords))
                continue;

            foreach (Match match in SleepDuration().Matches(sentence))
            {
                var minutes = Minutes(match);
                if (minutes is not { } stated || stated > MaxPlausibleNightMinutes)
                    continue;

                var candidates = KindOf(sentence, match) switch
                {
                    SleepFigureKind.Difference => supported.Differences,
                    SleepFigureKind.Reference => supported.References,
                    SleepFigureKind.Average => supported.Averages,
                    _ => supported.Lengths,
                };
                if (!candidates.Any(figure => Math.Abs(stated - figure) <= Tolerance(figure)))
                    return match.Value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The sleep figures a reply over some nights could honestly state, in minutes, by kind — see
    /// <see cref="StatesASleepFigureTheDataDoesNot"/> for why the kinds are kept apart.
    /// </summary>
    /// <param name="Nights">Each night's sleep.</param>
    /// <param name="Averages">The nights' averages, only when enough nights arrived to take one.</param>
    /// <param name="References">The member's usual and the published band's edges.</param>
    /// <param name="Differences">The distance from each night, average and reference to each reference.</param>
    internal sealed record SleepFigures(
        IReadOnlyCollection<decimal> Nights,
        IReadOnlyCollection<decimal> Averages,
        IReadOnlyCollection<decimal> References,
        IReadOnlyCollection<decimal> Differences)
    {
        /// <summary>Every figure of every kind but differences — what a plain length is held to.</summary>
        public IReadOnlyCollection<decimal> Lengths { get; } = [.. Nights, .. Averages, .. References];
    }

    /// <summary>
    /// The <see cref="SleepFigures"/> for these nights, this usual and this member's age.
    /// </summary>
    /// <remarks>
    /// Both averages because either is honest — the newest night is complete by the morning, but a
    /// reply may fairly set it apart as "last night" and average the rest.
    /// </remarks>
    /// <param name="nightsMinutes">The nights the reply was written over, oldest first.</param>
    /// <param name="usualMinutes">The member's usual, when one is learned.</param>
    /// <param name="averagesAreCovered">
    /// Whether enough of the window's nights arrived to average them
    /// (<see cref="ReadingWindowSummary.IsCovered"/>). When they did not, there are no averages: the
    /// window summary refused to give one, and a reply that states the average of three nights as
    /// the week's is the figure this guard exists to catch — a mixed question whose other reading
    /// cleared the bar still reaches the clinical read with sleep short of it.
    /// </param>
    /// <param name="ageYears">
    /// Picks the band the edges come from, the same one the prompt was shown: 7–8 hours from 65,
    /// 7–9 below. Every edge NSF publishes when the age is unknown, rather than rejecting a reply
    /// for quoting one of them.
    /// </param>
    internal static SleepFigures SupportedSleepFigures(
        IEnumerable<int> nightsMinutes, decimal? usualMinutes, bool averagesAreCovered, int? ageYears)
    {
        var nights = nightsMinutes.Select(n => (decimal)n).ToList();

        var averages = new List<decimal>();
        if (averagesAreCovered && nights.Count > 0)
            averages.Add(nights.Average());
        if (averagesAreCovered && nights.Count > 1)
            averages.Add(nights.SkipLast(1).Average());

        var references = new List<decimal>();
        if (ageYears is { } age)
        {
            var band = HealthReferenceRanges.Sleep(age);
            references.Add(band.Low * 60);
            references.Add(band.High * 60);
        }
        else
        {
            references.AddRange([7m * 60, 8m * 60, 9m * 60]);
        }

        if (usualMinutes is { } usual)
            references.Add(usual);

        var differences = new HashSet<decimal>();
        foreach (var figure in nights.Concat(averages).Concat(references))
        {
            foreach (var reference in references)
                differences.Add(Math.Abs(figure - reference));
        }

        return new SleepFigures(nights, averages, references, differences);
    }

    private enum SleepFigureKind
    {
        Length,
        Difference,
        Reference,
        Average,
    }

    /// <summary>
    /// What kind of figure the duration at <paramref name="match"/> is, from the words beside it —
    /// in this order of precedence, because "40 minutes less than his usual" is a difference and
    /// "his usual average of 5h 45m" is the usual.
    /// </summary>
    private static SleepFigureKind KindOf(string sentence, Match match)
    {
        var before = sentence[..match.Index];
        var after = sentence[(match.Index + match.Length)..];

        if (ComparativeAfter().IsMatch(after) || ByBefore().IsMatch(before))
            return SleepFigureKind.Difference;
        if (ReferenceBefore().IsMatch(before))
            return SleepFigureKind.Reference;
        if (AverageBefore().IsMatch(before) || AverageAfter().IsMatch(after))
            return SleepFigureKind.Average;

        return SleepFigureKind.Length;
    }

    /// <summary>A yardstick named within four words before the figure: "his usual 5h 45m", "at least 7 hours".</summary>
    [GeneratedRegex(@"\b(?:usual|usually|normal|normally|typical|typically|recommended|recommends?|range|guidelines?|floor|ceiling|least)\b(?:\W+\w+){0,3}?\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceBefore();

    /// <summary>An average named within three words before the figure: "averaged about 4h 48m".</summary>
    [GeneratedRegex(@"\b(?:averag\w*|mean)\b(?:\W+\w+){0,2}?\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex AverageBefore();

    /// <summary>A rate within two words after the figure: "4h 48m a night", "5 hours of sleep per night".</summary>
    [GeneratedRegex(@"^\W*(?:\w+\W+){0,2}?(?:(?:a|per|each|every)\s+night\b|nightly\b|on\s+average\b)", RegexOptions.IgnoreCase)]
    private static partial Regex AverageAfter();

    [GeneratedRegex(@"^\W*(?:\w+\W+){0,2}?(?:less|more|fewer|below|above|short|shorter|longer|under|over|than|up|down|extra)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ComparativeAfter();

    [GeneratedRegex(@"\b(?:by|down|up)\s+(?:about|around|roughly|nearly|almost|just|over|under)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ByBefore();

    /// <summary>The longest stated duration read as a night — anything longer is a span of time.</summary>
    private const decimal MaxPlausibleNightMinutes = 16 * 60;

    private static decimal Tolerance(decimal figure) => Math.Max(15m, figure * 0.1m);

    private static decimal? Minutes(Match match)
    {
        if (match.Groups["minutesOnly"].Success)
            return decimal.Parse(match.Groups["minutesOnly"].Value, CultureInfo.InvariantCulture);

        if (!decimal.TryParse(match.Groups["hours"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var hours))
            return null;

        var total = hours * 60;
        total += match.Groups["fraction"].Value switch
        {
            "¼" => 15,
            "½" => 30,
            "¾" => 45,
            _ => 0,
        };
        if (match.Groups["minutes"].Success)
            total += decimal.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture);

        return total;
    }

    /// <summary>
    /// A duration as a reply writes one: "5h 34m", "2h22m", "5 hours and 34 minutes", "6.5 hours",
    /// "6½ hours", or minutes alone. The unit is closed by a negative lookahead rather than a word
    /// boundary, because "2h22m" runs the hours straight into the minutes.
    /// </summary>
    [GeneratedRegex(
        @"(?<hours>\d+(?:\.\d+)?)(?<fraction>[¼½¾])?\s*(?:hours?|hrs?|h)(?![a-z])(?:\s*(?:and\s+)?(?<minutes>\d+)\s*(?:minutes?|mins?|m)(?![a-z]))?"
        + @"|(?<minutesOnly>\d+)\s*(?:minutes?|mins?|m)(?![a-z])",
        RegexOptions.IgnoreCase)]
    private static partial Regex SleepDuration();

    /// <summary>A sentence break: terminal punctuation followed by space, or a line break.</summary>
    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex Sentences();

    /// <summary>
    /// Collapses "heart rate variability" to "hrv" so the two heart families do not overlap.
    /// </summary>
    /// <remarks>
    /// The phrase contains the shorter family's own words, so a read that mentioned only
    /// variability satisfied a summary claiming the heart rate itself — "heart rate was higher"
    /// against "heart rate variability was lower" passed, though the read said nothing about the
    /// rate. Rewriting the phrase before matching is what keeps both families' word lists plain
    /// words: the alternative is a lookahead inside every heart-rate entry, which is a rule about
    /// this one collision written five times.
    /// </remarks>
    private static string SeparateVariability(string text) =>
        Regex.Replace(text, @"heart[\s-]*rate[\s-]*variability", "hrv", RegexOptions.IgnoreCase);

    /// <summary>
    /// The readings this platform takes, each with the everyday words a caregiver-facing sentence
    /// would use for it. Stems, matched on a word boundary, so "breath" covers "breathing" and
    /// "breaths" without covering "breathe" being absent from the read for grammatical reasons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three omissions are deliberate. There is no weather family: "under the weather" is an
    /// everyday phrase about a person, not a claim about a reading, and it appeared in the very
    /// card that prompted this guard. "Still" on its own is not in the stillness family — it is
    /// one of the commonest adverbs in English ("he is still resting"), and a family named by an
    /// adverb would reject far more sound copy than it caught. And "moved" is not in the activity
    /// family for the same reason: a summary saying someone moved bedrooms last week is using the
    /// family's own answer to read the day, which is exactly what the brief asks for.
    /// </para>
    /// <para>
    /// A reading absent from this list is never flagged. The guard's claim is only that copy
    /// naming one of these has to have been given it — not that it lists everything a summary may
    /// mention.
    /// </para>
    /// </remarks>
    private static readonly (string Family, string[] Words)[] ReadingFamilies =
    [
        ("oxygen", ["oxygen", "spo2", "saturation", "sats"]),
        ("breathing", ["breath", "breathing", "breaths", "respiratory", "respiration"]),
        ("sleep", ["sleep", "sleeping", "slept", "asleep", "overnight", "nap", "naps", "napped"]),
        ("heart rate", ["heart rate", "heartbeat", "heartbeats", "pulse", "bpm", "beats per minute"]),
        ("heart rate variability", ["variability", "hrv"]),
        ("activity", ["step", "steps", "walk", "walks", "walking", "active", "activity",
            "movement", "exercise", "exercising"]),
        ("stillness", ["stillness", "sitting still", "sedentary", "unbroken"]),
    ];

    /// <summary>
    /// What makes a sentence one about sleep for <see cref="StatesASleepFigureTheDataDoesNot"/>:
    /// the sleep family's words, plus "night", which is how a reply names the thing whose length
    /// it is quoting ("about 5h 30m a night").
    /// </summary>
    /// <remarks>Declared after <see cref="ReadingFamilies"/>, which it reads: static fields
    /// initialise in textual order.</remarks>
    private static readonly string[] SleepSentenceWords =
        [.. ReadingFamilies.Single(f => f.Family == "sleep").Words, "night", "nights", "nightly"];

    private static bool Mentions(string text, string[] words) =>
        words.Any(word => Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase));

    /// <summary>
    /// The pronouns that state a male subject. Word-bounded, so "his" does not fire on "history"
    /// and "he" does not fire on "her".
    /// </summary>
    /// <remarks>
    /// The reflexive is listed rather than left to the word boundary: "himself" is one word, so a
    /// pattern matching "him" never reaches it, and "CardiTrackCardiMember made the tea himself"
    /// states a sex without using any of the other three.
    /// </remarks>
    [GeneratedRegex(@"\b(?:he|him|his|himself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MalePronouns();

    /// <summary>
    /// The pronouns that state a female subject. One shorter than its counterpart looks, because
    /// "her" serves as both the object and the possessive; "herself" is here for the reason its
    /// male counterpart is.
    /// </summary>
    [GeneratedRegex(@"\b(?:she|her|hers|herself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FemalePronouns();
}
