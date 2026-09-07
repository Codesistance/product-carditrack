using System.Globalization;
using System.Text.RegularExpressions;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Member chat's code-assembled replies — the ladder's two zero-model-call rungs
/// (docs/technical/member_chat_routing.md §5), written in code precisely so no model can assemble
/// a different sentence. Beside <see cref="AdvisePicker"/>, <see cref="AdviseServability"/> and
/// <see cref="AlertDetailComposer"/> because they are the same kind of thing: reply-composition
/// policy with no I/O, testable without a host. <c>MemberChatService</c> fetches the rows and
/// resolves the first name; everything after that is a pure function of what it passes in.
/// </summary>
public static partial class MemberChatReplies
{
    /// <summary>
    /// The answer to "is he asleep now?" — the limit first, then the most recent thing actually
    /// recorded, so the answer is useful rather than only honest.
    /// </summary>
    /// <remarks>
    /// Leads with what cannot be seen because that is the part the caregiver has to know — a
    /// reading offered first would be read as the answer to the question they asked. Names the day
    /// a figure belongs to for the same reason: "4,200 steps" with no date invites exactly the
    /// present-tense reading this whole path exists to prevent.
    /// </remarks>
    public static string LiveStatusReply(string? firstName, IReadOnlyList<ActivityLog> recent, DateOnly today)
    {
        // "what they're doing" rather than a stand-in noun when there is no name: every
        // relationship word here would be invented, and "what them is doing" is what a bare
        // substitution produces.
        var subject = string.IsNullOrWhiteSpace(firstName) ? "they're" : $"{firstName} is";
        var opening =
            $"I can't see what {subject} doing right now — readings only reach me after their watch "
            + "has recorded and synced them, so there's nothing live here to check.";

        return LatestFigures(recent, today) is not { } latest
            ? opening + " I don't have any recent readings for them either."
            : $"{opening} The most recent I have is {latest.When}: {latest.Figures}.";
    }

    /// <summary>
    /// The readings on file, for a caregiver who asked how someone is rather than what they are
    /// doing this instant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same figures <see cref="LiveStatusReply"/> states, without the liveness disclaimer in
    /// front of them. That disclaimer is the right opening for "is he asleep now?" and the wrong
    /// one for "how is he today": it answers a question the caregiver did not ask, and spends the
    /// first forty words of the reply doing it. Which of the two runs is decided by the triage
    /// call's <c>isAboutThisMoment</c>, whose own prompt draws exactly this line — "a question
    /// about a period, however recent, is not this".
    /// </para>
    /// <para>
    /// Serves as the fallback when there is no current status line, rather than declining: §5's
    /// rule for this rung is that past the staleness ceiling it computes from readings, because
    /// unlike a suggestion there is always something to say.
    /// </para>
    /// </remarks>
    public static string LatestReadingsReply(
        string? firstName, IReadOnlyList<ActivityLog> recent, DateOnly today)
    {
        var subject = string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;

        return LatestFigures(recent, today) is not { } latest
            ? $"I don't have any recent readings for {subject} yet — they arrive once their watch "
              + "has recorded and synced them."
            : $"The most recent readings I have for {subject} are {latest.When}: {latest.Figures}.";
    }

    /// <summary>
    /// The stored status line as a chat answer: the dashboard's headline and sentence, then the
    /// latest figures they rest on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The persisted line is a caption. On the dashboard it sits under a headline and a tier
    /// colour, beside tiles carrying the day's numbers, and "Steps are very low today." reads
    /// correctly there because the hero above it says how much that matters and the tile beside
    /// it says what the number is. Served verbatim in a chat bubble it arrives with neither — a
    /// bare seven-word sentence to "how is Dad today", when the same question routed one rung
    /// higher gets a paragraph with figures in it (observed 2026-09-07).
    /// </para>
    /// <para>
    /// So the line keeps its place at the front, where the dashboard puts it, and the figures
    /// follow — the same dated figure list the other two status replies speak, through
    /// <see cref="LatestReadingsReply"/>, so the three cannot state a reading differently or date
    /// it differently. Still assembled in code: this rung makes no model call, and a caption
    /// plus figures is a sentence code can write.
    /// </para>
    /// </remarks>
    public static string StatusLineReply(
        string? firstName, MemberStatusLine line, IReadOnlyList<ActivityLog> recent, DateOnly today)
    {
        return $"{CaptionLead(line)} {LatestReadingsReply(firstName, recent, today)}";
    }

    /// <summary>The caption as the dashboard shows it: headline, then sentence.</summary>
    private static string CaptionLead(MemberStatusLine line)
    {
        var message = line.Message.Trim();
        var headline = line.Headline?.Trim();

        // The headline is documented as droppable — the dashboard keeps per-tier copy to fall
        // back on — so the reply must read whole without it.
        return string.IsNullOrWhiteSpace(headline) ? message : $"{headline} — {message}";
    }

    /// <summary>
    /// The status rung's answer, chosen from the question's own words: the reading it names, the
    /// full list if it asked for the readings, else the dashboard's line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §5 gives this rung a deterministic source rule — a named metric computes that value, no
    /// metric serves the stored line — and until now only the second half existed. "How is his
    /// heart rate" was answered "Steps are lower today than yesterday." (dev, 2026-09-07): the
    /// caption is a sentence about whichever reading the batch found most worth a sentence, and
    /// a question naming a different reading was answered about the wrong one, truthfully.
    /// </para>
    /// <para>
    /// A named reading leads, dated, with the previous day's value beside it — the comparison
    /// the caregiver is asking for when they ask "how is" a number. The caption follows only
    /// when it is about something else: a caption about the reading just stated is the same
    /// fact twice, and one about a different reading is the rest of the picture. A request for
    /// the readings themselves gets the readings, not the caption that summarises them.
    /// </para>
    /// </remarks>
    public static string StatusReply(
        string? firstName,
        string question,
        MemberStatusLine? line,
        IReadOnlyList<ActivityLog> recent,
        DateOnly today)
    {
        if (StatusQuestion.MetricNamed(question) is { } metric)
        {
            var reply = MetricReadingReply(firstName, metric, recent, today);
            return line is not null && StatusQuestion.MetricNamed(line.Message) != metric
                ? $"{reply} On the whole: {CaptionLead(line)}"
                : reply;
        }

        if (StatusQuestion.AsksForAllReadings(question) || line is null)
            return LatestReadingsReply(firstName, recent, today);

        return StatusLineReply(firstName, line, recent, today);
    }

    /// <summary>
    /// One reading, dated, with the previous day's value for comparison when there is one — the
    /// <see cref="LatestReadingsReply"/> shape restricted to the metric the caregiver named.
    /// </summary>
    public static string MetricReadingReply(
        string? firstName, StatusMetric metric, IReadOnlyList<ActivityLog> recent, DateOnly today)
    {
        var subject = string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;
        var name = MetricName(metric);

        var dated = recent
            .Where(l => Figure(metric, l) is not null)
            .OrderBy(l => l.Date)
            .ToList();

        if (dated.Count == 0)
        {
            return $"I don't have a recent {name} reading for {subject} — readings arrive once their "
                + "watch has recorded and synced them.";
        }

        var latest = dated[^1];
        var reply = $"The most recent {name} I have for {subject} is {Figure(metric, latest)}, "
            + When(metric, latest.Date, today);

        if (dated.Count > 1)
        {
            var previous = dated[^2];
            reply += $"; {When(metric, previous.Date, today)} it was {Figure(metric, previous)}";
        }

        return reply + ".";
    }

    /// <summary>The metric as a caregiver reads it — spelled once here, so a figure and its
    /// name cannot drift apart between replies.</summary>
    private static string MetricName(StatusMetric metric) => metric switch
    {
        StatusMetric.HeartRateVariability => "overnight heart rate variability",
        StatusMetric.RestingHeartRate => "resting heart rate",
        StatusMetric.Oxygen => "blood oxygen",
        StatusMetric.BreathingRate => "overnight breathing rate",
        StatusMetric.Sleep => "sleep",
        _ => "step count",
    };

    /// <summary>The metric's figure on one day, in its own unit, or null when that day has none.</summary>
    private static string? Figure(StatusMetric metric, ActivityLog log) => metric switch
    {
        StatusMetric.HeartRateVariability => log.HeartRateVariabilityMs is { } hrv
            ? $"{hrv.ToString("0", CultureInfo.InvariantCulture)} ms" : null,
        StatusMetric.RestingHeartRate => log.RestingHeartRate is { } hr ? $"{hr} bpm" : null,
        StatusMetric.Oxygen => log.SpO2Average is { } spo2
            ? $"{spo2.ToString("0.#", CultureInfo.InvariantCulture)}%" : null,
        // The overnight figure, as the charts and the bands block use; the daytime rate stands in
        // only for a device that records nothing overnight.
        StatusMetric.BreathingRate => (log.OvernightBreathingRate ?? log.BreathingRate) is { } br
            ? $"{br.ToString("0.#", CultureInfo.InvariantCulture)} breaths a minute" : null,
        StatusMetric.Sleep => log.SleepMinutes is { } sleep ? ReadingFigures.SleepFigure(sleep) : null,
        _ => log.Steps is { } steps ? $"{steps:#,##0} steps" : null,
    };

    /// <summary>
    /// The day a figure belongs to. Sleep is attributed to the morning it ended on, so a night on
    /// today's row is "last night" — the same rule every renderer follows, spelled for a chat
    /// reply rather than a heading.
    /// </summary>
    private static string When(StatusMetric metric, DateOnly date, DateOnly today)
    {
        if (metric != StatusMetric.Sleep)
            return DayLabel(date, today);

        return date == today
            ? "last night"
            : date == today.AddDays(-1)
                ? "the night before last"
                : $"the night ending {date.ToString("MMM d", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The newest day with anything recorded on it, as a dated figure list — shared by the two
    /// status replies so they cannot state the same readings differently.
    /// </summary>
    private static (string When, string Figures)? LatestFigures(
        IReadOnlyList<ActivityLog> recent, DateOnly today)
    {
        var latest = recent
            .Where(l => l.Steps is not null || l.RestingHeartRate is not null || l.SleepMinutes is not null
                        || l.HeartRateVariabilityMs is not null || l.OvernightBreathingRate is not null
                        || l.SpO2Average is not null)
            .OrderBy(l => l.Date)
            .LastOrDefault();

        if (latest is null)
            return null;

        // Every reading the day carries, not the first three: asked for "his specific
        // measurements", a list that stopped at sleep left the overnight figures out of an
        // answer whose whole point was completeness. Absent ones are omitted rather than named
        // — a device that derives none should not have the reply say so every time.
        var parts = new List<string>();
        if (latest.Steps is { } steps)
            parts.Add($"{steps:#,##0} steps");
        if (latest.RestingHeartRate is { } hr)
            parts.Add($"a resting heart rate of {hr} bpm");
        if (latest.SleepMinutes is { } sleep)
            parts.Add($"{ReadingFigures.SleepFigure(sleep)} of sleep the night before");
        if (latest.HeartRateVariabilityMs is { } hrv)
            parts.Add($"an overnight heart rate variability of {hrv.ToString("0", CultureInfo.InvariantCulture)} ms");
        if (latest.OvernightBreathingRate is { } breathing)
            parts.Add($"{breathing.ToString("0.#", CultureInfo.InvariantCulture)} breaths a minute overnight");
        if (latest.SpO2Average is { } spo2)
            parts.Add($"{spo2.ToString("0.#", CultureInfo.InvariantCulture)}% blood oxygen");

        return (DayLabel(latest.Date, today), Join(parts));
    }

    /// <summary>
    /// The one way this app spells the day a figure belongs to.
    /// </summary>
    /// <remarks>
    /// Cased for the middle of a sentence as each piece needs: the two relative words are
    /// lowercase there, a month name is not. Lowercasing the lot turned "Aug 19" into "aug 19",
    /// which reads as a typo and disagrees with every date the UI draws.
    /// <para>
    /// Public because the generated rungs need the same spelling. <see cref="LiveStatusReply"/>
    /// dated every figure it stated from the day this path was written; the analysis and inference
    /// replies did not, and the two rungs answering the same question minutes apart with figures
    /// from different days — neither named — is what this is shared to prevent.
    /// </para>
    /// </remarks>
    public static string DayLabel(DateOnly date, DateOnly today) => date == today
        ? "today so far"
        : date == today.AddDays(-1)
            ? "yesterday"
            : date.ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>The stretch a set of figures covers, in <see cref="DayLabel"/>'s vocabulary.</summary>
    /// <remarks>
    /// The far end keeps its relative word, so a span ending today still says "today so far" and
    /// carries the partial-day warning with it — "Aug 30 to today so far" is the honest reading of
    /// a window whose last day has not finished.
    /// </remarks>
    public static string SpanLabel(DateOnly from, DateOnly to, DateOnly today) => from == to
        ? DayLabel(from, today)
        : $"{from.ToString("MMM d", CultureInfo.InvariantCulture)} to {DayLabel(to, today)}";

    /// <summary>
    /// The span a clinical read says its figures came from, or null when that cannot be trusted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same closed-vocabulary discipline as every other parse of model output on this
    /// platform: the model picks <em>which</em> dates, this decides whether they are usable, and
    /// <see cref="DayAttribution"/> writes the words a caregiver reads. An unparseable date, or one
    /// outside the window that was actually fetched, is dropped rather than coerced — exactly as
    /// <c>ChatDataRegistry.CitationsFor</c> drops an authority it does not carry. Nothing claimed
    /// beats something invented, and a wrong date is worse than no date.
    /// </para>
    /// <para>
    /// Strict <c>yyyy-MM-dd</c>, not a lenient parse: a loose one accepts "Sep 4" and resolves the
    /// year from the current culture, which is how a reply ends up dated to a year with no
    /// readings in it.
    /// </para>
    /// </remarks>
    public static (DateOnly From, DateOnly To)? ResolveSpan(
        string? from, string? to, (DateOnly From, DateOnly To)? fetchedWindow)
    {
        // No activity fetched means no figures to date — an answer from member context and the
        // baseline alone has no day to name, and naming one would invent it.
        if (fetchedWindow is not { } window)
            return null;

        if (!DateOnly.TryParseExact(from?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(to?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end))
            return null;

        // A model that returns the pair the wrong way round has still told us which two days it
        // meant; the order is presentation, and correcting it costs nothing.
        if (end < start)
            (start, end) = (end, start);

        return start < window.From || end > window.To ? null : (start, end);
    }

    /// <summary>
    /// A reply with the day its figures belong to stated in code, when the reply has not already
    /// said so itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appended rather than woven in, for the reason <c>AdviseReply</c> quotes its authority at the
    /// end: a sentence assembled here cannot be a sentence the rewrite model composed, and the
    /// rewrite model is the step that dropped the day in the first place. The clinical prompt
    /// already labels every row it is given ("Today so far (…partial)", "Yesterday (…complete
    /// day)"); it is the rewrite that turned "yesterday, complete" into "a stable day".
    /// </para>
    /// <para>
    /// Suppressed when the reply already names the day — the same conditional-append shape
    /// <c>AdviseReply</c> uses for its doctor line, and for the same reason: one statement is the
    /// framing, two is a stutter. The marker is derived from the <em>validated window</em>, never
    /// from the reply, so a reply that says "today" about yesterday's figures is corrected rather
    /// than left alone.
    /// </para>
    /// </remarks>
    public static string WithDayAttribution(
        string reply, DateOnly from, DateOnly to, DateOnly today)
    {
        // The bare day word, because a reply saying "yesterday" has dated itself even though it
        // did not spell the label in full.
        //
        // Today is the one day that shortcut cannot take. "today so far" says the day is
        // unfinished and the total will still climb; bare "today" does not, and a running step
        // count read as a finished one is precisely the misreading this path exists to prevent —
        // it is why DayLabel spells today differently from every other day in the first place. So
        // a reply that says only "today" has not dated a partial day, and the sentence still goes
        // on. Erring toward appending is the safe direction here: a caregiver told twice that the
        // day is unfinished has lost nothing, and one told once that it is finished has.
        var marker = from == to && from != today
            ? from == today.AddDays(-1)
                ? "yesterday"
                : from.ToString("MMM d", CultureInfo.InvariantCulture)
            : SpanLabel(from, to, today);

        if (reply.Contains(marker, StringComparison.OrdinalIgnoreCase))
            return reply;

        var label = SpanLabel(from, to, today);
        var sentence = from == to
            ? $"Those figures are for {label}."
            : $"Those figures cover {label}.";

        return $"{reply}\n\n{sentence}";
    }

    /// <summary>
    /// A verdict held to the dashboard hero above it: when the hero is Yellow or worse and the
    /// reply reads as settled, the status line leads and the reply follows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Anything to follow up on?" was answered "Everything looks settled…" under a Yellow hero
    /// whose line read "Steps are very low today." (2026-09-07). The inference read sees what its
    /// planner fetched, and the hero tier rests partly on things outside that vocabulary — today's
    /// family digest urgency, the fresh hour assessment — so the verdict had nothing in front of
    /// it to disagree with. The clinical brief now carries the tier and the rule; this is the code
    /// behind the rule, for the reason every guard on this platform exists: a prompt rule
    /// forbidding a claim does not hold (docs/technical/member_chat_routing.md §9).
    /// </para>
    /// <para>
    /// Leads with the line rather than rewriting the verdict, because the verdict is the model's
    /// sentence and this must not compose a different one out of it. What the caregiver reads
    /// first is what the dashboard is already telling them, in the app's own words; the reply
    /// stands after it as the readings' view. Below Yellow nothing is touched — the hero is
    /// settled too, and a reply agreeing with it needs no correction.
    /// </para>
    /// <para>
    /// The pattern is deliberately whole-picture — "settled", "nothing needs attention",
    /// "everything looks fine" — not every reassuring clause. A reply that says one reading looks
    /// steady is not claiming the day is. And it is affirmative only: "not settled", "nothing is
    /// settled yet" already agree with the hero, and leading them with the status line would say
    /// the same thing twice — the first time in the app's voice and the second in the model's.
    /// </para>
    /// </remarks>
    public static string ReconcileWithStatusTier(string reply, AlertSeverity tier, MemberStatusLine? statusLine)
    {
        if (tier < AlertSeverity.Yellow || !SettledClaim().IsMatch(reply))
            return reply;

        var caption = statusLine?.Message.Trim();
        var lead = string.IsNullOrWhiteSpace(caption)
            ? "The dashboard is showing something worth attention today, so I wouldn't call things settled."
            : $"{caption} The dashboard is showing that as worth attention today, so I wouldn't call "
              + "things settled.";

        return $"{lead}\n\n{reply}";
    }

    /// <summary>
    /// A reply saying the whole picture is fine — the claim a Yellow hero contradicts. The
    /// lookbehind on "settled" is what keeps it a claim: a negation in front of the word — "not",
    /// "isn't", "nothing is", "far from" — turns it into agreement with the hero, and agreement is
    /// left alone. The negation may sit up to two words back ("not yet settled", "not really
    /// settled"): .NET lookbehind is variable-length, so the exclusion reaches as far as the
    /// phrasing does rather than only to the word immediately before.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:(?<!\b(?:not|never|hardly|isn't|aren't|wasn't|far from|less than|nothing(?:'s| is| looks| seems| feels))(?: \w+){0,2} )settled"
        + @"|no concerns?|nothing(?: \w+){0,2} (?:needs?|stands? out|to follow|to worry|to flag|to watch)"
        + @"|(?:everything|all|things) (?:looks?|seems?|is|are) (?:fine|good|okay|ok|steady|calm|normal|well))\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SettledClaim();

    /// <summary>
    /// True when the message carries nothing a question could be made of: an email address or a
    /// URL on its own, or a line with no letter in it at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Judged in code, ahead of the router, because the router cannot be taught this. Everything
    /// it renders is a way of answering, and a message that asks nothing fits none of them — so
    /// a bare email address fell to <c>steer.offtopic</c>, whose brief asserts the request is a
    /// health question about something unrecorded, and the caregiver was told their address was
    /// "a very reasonable health question" that the wearable does not track (2026-09-07). Three
    /// model calls to misdescribe a string the app could have recognised before the first.
    /// </para>
    /// <para>
    /// Narrow on purpose. "hi", "ok?", "thanks" and a lone question mark after a word all carry a
    /// word, and the router already places those as <c>steer.casual</c>; this catches only what
    /// no purpose line could ever place. Erring narrow is the safe direction: a non-question that
    /// slips through still gets the steer, whose brief now knows to say it caught no question,
    /// while a real question caught here would be answered with a nudge.
    /// </para>
    /// </remarks>
    public static bool CarriesNoQuestion(string message)
    {
        var trimmed = message.Trim();
        return AddressOnly().IsMatch(trimmed) || !AnyLetter().IsMatch(trimmed);
    }

    /// <summary>The whole message is one email address or one URL, trailing punctuation aside.</summary>
    [GeneratedRegex(@"^(?:[^\s@]+@[^\s@]+\.[^\s@]+|(?:https?://|www\.)\S+)[.,;:!?)]*$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressOnly();

    /// <summary>Any letter in any script — the least a question can be made of.</summary>
    [GeneratedRegex(@"\p{L}")]
    private static partial Regex AnyLetter();

    /// <summary>
    /// The nudge for a message with no question in it: what was missing, and what to ask instead
    /// — the same "what I can help with" every steer closes on, without a model to write it.
    /// </summary>
    public static string NotAQuestionReply(string? firstName)
    {
        // "their" rather than an invented relationship word, for the reason LiveStatusReply's
        // subject line gives at length.
        var whose = string.IsNullOrWhiteSpace(firstName) ? "their" : $"{firstName}'s";
        return $"I didn't catch a question there — ask me about {whose} sleep, activity, heart rate or alerts.";
    }

    /// <summary>Oxford-less list joining — "a, b and c".</summary>
    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "nothing recorded",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    /// <summary>
    /// The stored suggestion as one caregiver-facing reply, or an honest "nothing right now" when
    /// there is no current row to serve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opens with the suggestion, not the summary. The question this rung answers is "what could
    /// I do?", and a reply that led with three sentences of readings before getting to the one
    /// actionable sentence read as not answering it — the summary still travels, as the grounding
    /// after the answer rather than a preamble before it.
    /// </para>
    /// <para>
    /// Closes by marking what it just said as a suggestion — but only when the stored suggestion
    /// has not already done so itself. The generation prompt's <c>ToneWellnessNotClinical</c> asks
    /// for "worth mentioning to their doctor", so most rows arrive with a doctor line of their
    /// own, and appending this one unconditionally told the caregiver to see the doctor twice in
    /// three sentences. One line is the framing; two is a nag. A suggestion arriving in
    /// the same voice that answered "how did he sleep" a moment earlier is the one place on this
    /// platform where a caregiver could most easily read guidance as an instruction, and the card
    /// on CardiMember Details has a heading and a layout to carry that framing where a chat bubble
    /// has neither.
    /// </para>
    /// <para>
    /// The authority behind the suggestion is quoted at the end, as a References line — the same
    /// convention the inference rung closes with (decision 2026-08-24, reversing the earlier
    /// no-citation-in-chat choice). Not woven into the prose: "based on Adult physical activity"
    /// mid-sentence is what made an early version read like a leaflet. The quoted text is
    /// <see cref="WellnessGuidelines"/>' fixed lines, mapped from the stored
    /// <see cref="MemberAdvise.GuidelineCited"/> — the model picked which reference at generation
    /// time; code decides the words a caregiver reads, and a pick the closed set does not carry
    /// quotes nothing rather than something invented.
    /// </para>
    /// <para>
    /// A row with no <see cref="MemberAdvise.GuidelineCited"/> is still treated as nothing to serve
    /// rather than served bare — the same call <c>AdviseGenerationService</c> makes when it
    /// withholds such a row, and what <see cref="DTOs.Responses.AdviseResponse.GuidelineCited"/>
    /// tells clients to do with a null. That rule lives in <see cref="AdviseServability"/> rather
    /// than here: stated only in this method it made chat disagree with the Details card and the
    /// Dashboard pulse dot, which went on rendering such a row and lighting for it.
    /// </para>
    /// <para>
    /// The empty case says why there is nothing and what can be asked instead, rather than only
    /// declining: an advice question is one a caregiver asks when they are worried, and "no" on its
    /// own is the least useful moment to be terse with them.
    /// </para>
    /// </remarks>
    public static string AdviseReply(
        string? firstName, MemberAdvise? advise, DateTime utcNow, string? question = null)
    {
        // "them" rather than an invented relationship word, for the reason LiveStatusReply's
        // subject line gives at length.
        var subject = string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;

        if (!AdviseServability.IsServable(advise, utcNow))
        {
            return $"I don't have a suggestion for {subject} right now — those come from their "
                + "readings once a day, and there isn't a current one. I can tell you how their "
                + "sleep, activity or heart rate compare with what's usual for them, though.";
        }

        var reply = $"{advise.Suggestion.Trim()} {advise.Summary.Trim()}";

        // Asked "what kind of exercises can he do", this rung served the stored activity row
        // verbatim — "add more movement, like short walks during breaks". The caregiver asked
        // WHICH; they were answered DO MORE, with nothing to tell them the two were different
        // questions. The row stays as it is: advise is never generated per question, and that is
        // what earns it the only suggestion licence on this platform. What was missing was
        // honesty about fit.
        if (question is not null && AdvisePicker.AsksForSpecifics(question))
        {
            reply += $" That's the standing suggestion for {subject} rather than an answer to "
                + "exactly what you asked — for what specifically would suit them, their doctor "
                + "is the one to ask.";
        }

        if (!DoctorMention().IsMatch(reply))
        {
            reply += " That's just an idea to consider — their doctor is the one to ask if "
                + "you're unsure about it.";
        }

        return WellnessGuidelines.CitationFor(advise.GuidelineCited) is { } citation
            ? $"{reply}\n\nReference: {citation}."
            : reply;
    }

    /// <summary>The stored row already routes to a clinician, in whichever word the model chose —
    /// what makes the appended framing line redundant rather than required.</summary>
    [GeneratedRegex(@"\b(?:doctors?|GPs?|physicians?|clinicians?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoctorMention();
}
