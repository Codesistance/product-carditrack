using System.Diagnostics;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// When this launch began, so the splash can hold the brand for a fixed span measured from
/// launch rather than adding a fixed span on top of it. The OS splash is already showing the
/// logo before any of our code runs; time it spends on screen counts toward the floor.
/// </summary>
/// <remarks>
/// Anchored at <see cref="MauiProgram.CreateMauiApp"/> — the earliest managed entry point we
/// control on every platform. That is a little after the real process start, so the measured
/// elapsed time is an under-estimate and the hold errs long, never short.
/// </remarks>
internal static class AppStartup
{
    private static long _startedAt;

    /// <summary>Records the launch instant. Later calls are ignored.</summary>
    internal static void Mark()
    {
        if (_startedAt == 0)
            _startedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Time since <see cref="Mark"/>, or zero if it was never called.</summary>
    internal static TimeSpan Elapsed =>
        _startedAt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_startedAt);
}
