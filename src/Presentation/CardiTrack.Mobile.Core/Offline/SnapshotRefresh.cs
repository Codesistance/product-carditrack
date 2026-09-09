using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// Stale-while-revalidate for one screen load: put the device's saved answer on screen at once,
/// fetch the live one behind it, and replace when it lands. A screen used to open onto a
/// skeleton on every landing while the previous answer sat encrypted on the device the whole
/// time; the skeleton is now only for a question the device has never answered.
/// </summary>
/// <remarks>
/// <para>
/// Await this from the UI thread. Every callback — <paramref name="render"/> and the three on
/// <see cref="IRefreshFeedback"/> — runs on the caller's synchronization context, which is why
/// nothing in Mobile.Core may use <c>ConfigureAwait(false)</c>: the page's own load method
/// relies on the same thing, and this is that method with the sequence lifted out.
/// </para>
/// <para>
/// Faults in <paramref name="render"/> propagate. A screen that cannot draw what it was given
/// has its own catch for that (and a reason to show its error panel); swallowing it here would
/// leave the page saying "checking for updates" for the rest of the session.
/// </para>
/// </remarks>
public static class SnapshotRefresh
{
    /// <summary>
    /// The single-GET form: <paramref name="peek"/> and <paramref name="fetch"/> each make one
    /// client call, and the run tracks it for provenance itself.
    /// </summary>
    /// <param name="peek">
    /// The cache-only read for exactly the question <paramref name="fetch"/> asks, or null when
    /// the screen already has data on it — a resume, a timer tick, a pull — and should replace
    /// in place without a snapshot detour.
    /// </param>
    /// <param name="sameAs">
    /// Says whether the live answer is the snapshot over again. When it is, nothing is
    /// re-rendered and nothing is announced: a journal entry from last week never changes, and
    /// "Updating…" over identical content is noise.
    /// </param>
    public static Task<RefreshOutcome> RunAsync<T>(
        ICardiTrackApiClient api,
        LoadGate gate,
        LoadTicket ticket,
        Func<CancellationToken, Task<T?>>? peek,
        Func<CancellationToken, Task<T>> fetch,
        Action<T> render,
        IRefreshFeedback feedback,
        Func<T, T, bool>? sameAs = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(fetch);
        return RunAsync(
            api, gate, ticket,
            peek is null ? null : (ct, scope) => scope.Track(peek(ct)),
            (ct, scope) => scope.Track(fetch(ct)),
            render, feedback, sameAs);
    }

    /// <summary>
    /// The composite form, for a screen whose one render needs more than one GET. The
    /// delegates receive the phase's <see cref="RefreshScope"/> and must
    /// <see cref="RefreshScope.Track"/> every client call they make, or the run cannot tell
    /// where the answer came from.
    /// </summary>
    public static async Task<RefreshOutcome> RunAsync<T>(
        ICardiTrackApiClient api,
        LoadGate gate,
        LoadTicket ticket,
        Func<CancellationToken, RefreshScope, Task<T?>>? peek,
        Func<CancellationToken, RefreshScope, Task<T>> fetch,
        Action<T> render,
        IRefreshFeedback feedback,
        Func<T, T, bool>? sameAs = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(feedback);

        var ct = ticket.Token;
        T? saved = null;
        DateTimeOffset? savedAt = null;

        try
        {
            if (peek is not null)
            {
                var peekScope = new RefreshScope();
                saved = await peek(ct, peekScope);
                if (!gate.IsCurrent(ticket))
                    return RefreshOutcome.Superseded;

                if (saved is not null)
                {
                    // The banner first, then the snapshot under it: a screen must never have
                    // saved health data up, even for a frame, without saying so — and a render
                    // that reads the banner's state to decide its own (the dashboard's
                    // stale-sync notice) needs it already set.
                    savedAt = peekScope.OldestCachedAt(api);
                    feedback.SavedShown(savedAt);
                    render(saved);
                }
            }

            var fetchScope = new RefreshScope();
            var fresh = await fetch(ct, fetchScope);
            if (!gate.IsCurrent(ticket))
                return RefreshOutcome.Superseded;

            if (fetchScope.AnyCached(api))
            {
                // The client could not reach the API and answered from the device instead. With
                // a snapshot already up that is the same data over again; without one it is the
                // first thing the screen has to show.
                if (saved is null)
                    render(fresh);
                return Complete(feedback,
                    new RefreshOutcome(RefreshResult.SavedOnlyOffline, fetchScope.OldestCachedAt(api) ?? savedAt, null));
            }

            if (saved is not null && sameAs?.Invoke(saved, fresh) == true)
                return Complete(feedback, new RefreshOutcome(RefreshResult.FreshUnchanged, null, null));

            if (saved is not null)
                feedback.Replacing();
            render(fresh);
            return Complete(feedback, new RefreshOutcome(
                saved is null ? RefreshResult.FreshNoSnapshot : RefreshResult.FreshReplacedSaved, null, null));
        }
        catch (OperationCanceledException) when (!gate.IsCurrent(ticket))
        {
            // Cancellation during a body read is not wrapped as ApiException; a superseded
            // load reports it the same way either arrives.
            return RefreshOutcome.Superseded;
        }
        catch (ApiException ex)
        {
            // A superseded request reports its cancellation as a transport failure. That is the
            // screen's own doing and must not surface as "no connection".
            if (!gate.IsCurrent(ticket))
                return RefreshOutcome.Superseded;

            // A 404 over a snapshot means the thing is gone; the snapshot must not outlive it.
            if (saved is null || ex.IsNotFound)
                return Complete(feedback, new RefreshOutcome(RefreshResult.NothingAndFailed, null, ex));

            return Complete(feedback, new RefreshOutcome(
                ex.IsNetworkFailure ? RefreshResult.SavedOnlyOffline : RefreshResult.SavedOnlyHttpError, savedAt, ex));
        }
    }

    private static RefreshOutcome Complete(IRefreshFeedback feedback, RefreshOutcome outcome)
    {
        feedback.Completed(outcome);
        return outcome;
    }
}
