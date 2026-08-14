using System.Text.Json;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// Turns "which gaps are open right now" into changes against what is already stored.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>diff, not an append</b>. The desired set is computed from the rules, matched against
/// stored rows by fingerprint, and the difference applied: new gaps are inserted, continuing ones
/// are refreshed, and gaps that are no longer present are resolved. The last of those is the point
/// — <b>a user never has to dismiss a nudge they have already fixed</b>, which is what keeps the
/// inbox honest enough to be worth opening.
/// </para>
/// <para>
/// Pure and deterministic: it takes stored rows plus contexts and returns a plan. Nothing here
/// reads a clock or a database, so "run it twice, get the same answer" is a unit test rather than
/// an aspiration.
/// </para>
/// </remarks>
public static class NudgeReconciler
{
    /// <summary>
    /// Cap on new rows per user per run. Without it, a newly authored rule set drops a dozen cards
    /// on someone the morning it ships; the surplus is simply detected again next run.
    /// </summary>
    public const int MaxNewPerUserPerRun = 3;

    public sealed record Plan
    {
        public List<Notification> ToInsert { get; } = [];
        public List<Notification> ToUpdate { get; } = [];
        public int SuppressedCount { get; set; }

        public int ResolvedCount => ToUpdate.Count(n =>
            n.State is NotificationState.Resolved or NotificationState.Superseded);
    }

    /// <summary>
    /// Reconciles one user's stored notifications against every context they own.
    /// </summary>
    /// <param name="contexts">
    /// All contexts for this user — one per member they are the owner for, plus the member-less
    /// account context. Must share a <c>UtcNow</c>.
    /// </param>
    /// <param name="stored">Every stored row for the user, in any state.</param>
    public static Plan Reconcile(
        IReadOnlyList<NudgeContext> contexts,
        IReadOnlyList<Notification> stored,
        IReadOnlyList<INudgeRule>? rules = null)
    {
        var plan = new Plan();
        if (contexts.Count == 0)
            return plan;

        rules ??= NudgeRuleCatalogue.All;
        var utcNow = contexts[0].UtcNow;

        var storedByFingerprint = stored
            .GroupBy(n => n.Fingerprint)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var desired = new Dictionary<string, (NudgeContext Context, INudgeRule Rule, NudgeVerdict Verdict)>(
            StringComparer.Ordinal);

        foreach (var context in contexts)
        {
            foreach (var rule in rules)
            {
                if (IsSuppressed(context, rule))
                {
                    plan.SuppressedCount++;
                    continue;
                }

                var verdict = rule.Evaluate(context);
                if (!verdict.HasGap)
                    continue;

                var fingerprint = NudgeFingerprint.Compute(
                    rule.RuleCode, context.User.Id, context.ScopeId, verdict.Discriminator);

                desired[fingerprint] = (context, rule, verdict);
            }
        }

        // Present now, and already stored — refresh in place, uncapped. The cap exists to limit how
        // much lands on someone in one morning, not to stop existing rows being kept current.
        var candidates = new List<Notification>();
        foreach (var (fingerprint, (context, rule, verdict)) in desired)
        {
            if (storedByFingerprint.TryGetValue(fingerprint, out var existing))
            {
                Refresh(existing, verdict, rule, utcNow);
                plan.ToUpdate.Add(existing);
                continue;
            }

            candidates.Add(Build(fingerprint, context, rule, verdict, utcNow));
        }

        // Rank first, then cap. Ordering the survivors afterwards would be too late — whichever
        // fingerprints happened to enumerate first would already have taken the three slots, so a
        // Critical could be dropped in favour of a Low and merely sorted correctly among the rest.
        // Ties break on fingerprint so the same three win on a re-run over the same inputs.
        candidates.Sort((a, b) =>
        {
            var byPriority = a.Priority.CompareTo(b.Priority);
            return byPriority != 0 ? byPriority : string.CompareOrdinal(a.Fingerprint, b.Fingerprint);
        });

        plan.ToInsert.AddRange(candidates.Take(MaxNewPerUserPerRun));

        // Absent now — the user fixed it, or it stopped applying.
        foreach (var existing in stored)
        {
            if (desired.ContainsKey(existing.Fingerprint))
                continue;

            if (existing.State is NotificationState.Resolved or NotificationState.Superseded)
                continue;

            existing.State = NotificationState.Resolved;
            existing.ResolutionReason = NotificationResolutionReason.GapClosed;
            existing.ResolvedDate = utcNow;
            existing.LastEvaluatedDate = utcNow;
            plan.ToUpdate.Add(existing);
        }

        return plan;
    }

    /// <summary>
    /// Whether this rule should not even be evaluated for this context. Suppression is checked
    /// before <see cref="INudgeRule.Evaluate"/> so a muted or paused scope costs nothing.
    /// </summary>
    private static bool IsSuppressed(NudgeContext context, INudgeRule rule)
    {
        var spec = rule.Spec;

        // Muted rules produce no row at all, rather than a hidden one. An inbox that accumulates
        // invisible history the user can never see or clear is worse than no record.
        var memberId = context.Member?.Id;
        if (context.Mutes.Any(m => m.Silences(rule.RuleCode, spec.Category, memberId, context.UtcNow)))
            return true;

        if (context.IsMemberPaused && !spec.AppliesWhenPaused)
            return true;

        // The inverse of AppliesWhenPaused: a pause rule has nothing to say about a live member.
        if (spec.AppliesWhenPaused && !context.IsMemberPaused)
            return true;

        if (context.IsInGracePeriod && !spec.AppliesDuringGracePeriod)
            return true;

        if (context.Member?.HasUnacknowledgedRedAlert == true && !spec.AppliesDuringRedAlert)
            return true;

        return false;
    }

    private static Notification Build(
        string fingerprint, NudgeContext context, INudgeRule rule, NudgeVerdict verdict, DateTime utcNow)
        => new()
        {
            OrganizationId = context.OrganizationId,
            UserId = context.User.Id,
            CardiMemberId = context.Member?.Id,
            RuleCode = rule.RuleCode,
            RuleVersion = rule.Version,
            Category = rule.Spec.Category,
            Priority = verdict.Priority ?? rule.Spec.Priority,
            Fingerprint = fingerprint,
            TitleKey = NudgeRuleCatalogue.TitleKey(rule.RuleCode, verdict.Variant),
            BodyKey = NudgeRuleCatalogue.BodyKey(rule.RuleCode, verdict.Variant),
            BenefitKey = NudgeRuleCatalogue.BenefitKey(rule.RuleCode, verdict.Variant),
            TemplateData = Serialize(verdict.TemplateData),
            ActionDeepLink = verdict.ActionDeepLink,
            State = NotificationState.Open,
            IsOwner = context.IsOwner,
            FirstDetectedDate = utcNow,
            LastEvaluatedDate = utcNow
        };

    /// <summary>
    /// Updates a row whose gap is still present. <see cref="Notification.LastEvaluatedDate"/> always
    /// moves — a rule that silently stops being evaluated should be visible as a stale timestamp,
    /// not inferred from an absence.
    /// </summary>
    private static void Refresh(Notification existing, NudgeVerdict verdict, INudgeRule rule, DateTime utcNow)
    {
        existing.LastEvaluatedDate = utcNow;

        // A snooze that has run out reopens on its own; a live one is left strictly alone.
        if (existing.State == NotificationState.Snoozed && existing.SnoozedUntil <= utcNow)
        {
            existing.State = NotificationState.Open;
            existing.SnoozedUntil = null;
        }

        if (existing.State is NotificationState.Resolved or NotificationState.Superseded)
        {
            // A dismissal is a decision, and it outlasts the gap coming back. Only a version bump —
            // us changing the offer — earns the right to ask again.
            var wasDismissed = existing.ResolutionReason == NotificationResolutionReason.Dismissed;
            if (wasDismissed && rule.Version <= existing.RuleVersion)
                return;

            // Everything else resolved because the gap went away. If it has returned — a device
            // reconnected and then broke again — the user genuinely needs telling a second time.
            existing.State = NotificationState.Open;
            existing.ResolvedDate = null;
            existing.ResolutionReason = null;
            existing.FirstSeenDate = null;
            existing.RuleVersion = rule.Version;
        }

        // Progress counters move while the gap stays open — "4/30 days" becoming "11/30".
        existing.TemplateData = Serialize(verdict.TemplateData);

        // Same fingerprint, worse gap: a device connection keeps its id as its battery keeps
        // draining, so a tier change (warning -> urgent -> critical) refreshes this same row
        // rather than opening a new one. Without this a notification raised at "warning" would
        // sit at that wording and priority for as long as the caregiver leaves it open, even as
        // the battery — and the actual urgency — kept falling.
        existing.Priority = verdict.Priority ?? rule.Spec.Priority;
        existing.TitleKey = NudgeRuleCatalogue.TitleKey(rule.RuleCode, verdict.Variant);
        existing.BodyKey = NudgeRuleCatalogue.BodyKey(rule.RuleCode, verdict.Variant);
        existing.BenefitKey = NudgeRuleCatalogue.BenefitKey(rule.RuleCode, verdict.Variant);
    }

    private static string Serialize(IReadOnlyDictionary<string, object> templateData) =>
        templateData.Count == 0 ? "{}" : JsonSerializer.Serialize(templateData);
}
