namespace CardiTrack.Mobile.Services;

/// <summary>
/// When a load started by a <c>[QueryProperty]</c> setter actually runs: after Shell has finished
/// handing over the whole route, not partway through it.
/// </summary>
/// <remarks>
/// <para>
/// Shell applies a page's query properties in one synchronous pass, one setter after another, in
/// the order the attributes are declared. <c>memberId</c> is declared first on every member-scoped
/// page, so a load kicked off from that setter starts while the rest of the route — <c>canManage</c>,
/// <c>cadence</c>, <c>alarmId</c>, <c>focus</c>, <c>name</c> — is still unset.
/// </para>
/// <para>
/// A load reads those. It is not a hypothetical: <c>JournalEntryPage</c> captures the cadence into
/// a tuple before its first <c>await</c>, so a Weekbook route would have loaded a Daybook and never
/// corrected itself, and <c>CardiMemberDetailPage</c> snapshots the focus flag <em>and clears it</em>,
/// so <c>?focus=advise</c> would have lost the scroll it asked for. The rest read theirs after an
/// await and so happen to survive — but only because that await yields, which is exactly the kind
/// of ordering <see cref="Core.Navigation.MemberRoute"/> exists to stop depending on.
/// </para>
/// <para>
/// Dispatching puts the load on the UI thread's queue instead of running it inline, and the pass
/// in progress is itself UI-thread work, so every remaining setter has run by the time the load
/// starts. One turn of the message loop later than before, and no page needs to track the arrival
/// of anything but its id.
/// </para>
/// </remarks>
internal static class RouteArrival
{
    /// <summary>
    /// Runs <paramref name="load"/> once the route this page was opened with has been handed over
    /// in full.
    /// </summary>
    public static void WhenRouteHasLanded(this Element page, Action load) =>
        page.Dispatcher.Dispatch(load);
}
