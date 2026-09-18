using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Devices;
using CardiTrack.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>
/// The caregiver's side of a wearer invitation: the QR code to hold up, or the link to send, plus
/// what the wearer has done so far and how long is left.
/// </summary>
/// <remarks>
/// <para>
/// The screen exists because the wearer is somewhere else. <strong>Nothing is sent from here on its
/// own.</strong> The link is generated and shown; handing it over is the caregiver's own tap, to
/// whichever app and whichever person they choose, so CardiTrack never learns who it went to.
/// </para>
/// <para>
/// <see cref="DeviceInviteWatch"/> holds the decisions: which state a status string means, when to
/// poll again, when a deadline has passed. This class renders that and nothing more, which is why
/// the rules are unit-tested without a device in the loop.
/// </para>
/// </remarks>
public partial class InviteWaitPage : ContentPage
{
    private readonly ICardiTrackApiClient _api;
    private readonly ILogger<InviteWaitPage> _logger;
    private readonly WizardContext _ctx;
    private readonly CardiMemberResponse _member;
    private readonly ConnectableDevice _device;
    private readonly bool _isQr;

    private DeviceInviteResponse _invite;
    private DeviceInviteWatch _watch;
    private CancellationTokenSource? _polling;

    /// <summary>
    /// The invitation URL, kept here rather than read off <see cref="_invite"/>.
    /// </summary>
    /// <remarks>
    /// The API returns the URL exactly once, in the response that created the invitation — a status
    /// read deliberately leaves it null so that polling cannot keep re-issuing a live credential.
    /// This screen polls, so the field it renders from has to be the one that does not change
    /// underneath it; reading the latest response would blank the link on the first poll.
    /// </remarks>
    private string? _url;

    /// <summary>Stops a second tap stacking another share sheet on top of the first.</summary>
    private bool _sharing;

    /// <summary>
    /// Cancelled when this page goes away, so a request started on it stops speaking for it —
    /// separate from <see cref="_polling"/>, which is the loop's own token.
    /// </summary>
    private CancellationTokenSource? _alive;

    public InviteWaitPage(
        WizardContext ctx, ConnectableDevice device, DeviceInviteResponse invite, bool isQr)
    {
        InitializeComponent();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
        _logger = ServiceHelper.GetRequiredService<ILogger<InviteWaitPage>>();
        _ctx = ctx;
        _member = ctx.RequireMember();
        _device = device;
        _invite = invite;
        _url = invite.Url;
        _isQr = isQr;
        _watch = new DeviceInviteWatch(invite.ExpiresAt);

        Header.Title = $"{device.DisplayName} Connection";
        Present();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _alive ??= new CancellationTokenSource();
        StartPolling();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopPolling();
        _alive?.Cancel();
        _alive?.Dispose();
        _alive = null;
    }

    /// <summary>
    /// Renders whatever the watch currently says. Called on every change rather than patching
    /// individual controls, so a state can never be half-applied.
    /// </summary>
    private void Present()
    {
        var name = NameFormatting.FirstName(_member.Name);

        QrPlate.IsVisible = _isQr && !_watch.IsFinished;
        LinkPlate.IsVisible = !_isQr && !_watch.IsFinished;

        if (_isQr && QrImage.Source is null && InviteQrCode.Render(_url) is { } png)
            QrImage.Source = ImageSource.FromStream(() => new MemoryStream(png));

        if (!_isQr)
            LinkLabel.Text = _url ?? string.Empty;

        TitleHeading.Text = _watch.State switch
        {
            DeviceInviteWatchState.Connected => "All set",
            DeviceInviteWatchState.Declined => $"{name} said it wasn't them",
            DeviceInviteWatchState.Expired => "That link has expired",
            DeviceInviteWatchState.Cancelled => "Cancelled",
            _ => _isQr ? "Ask them to scan this" : $"Send this to {name}",
        };

        SubHeading.Text = _watch.State switch
        {
            DeviceInviteWatchState.Connected =>
                $"{name}'s {_device.DisplayName} is connected.",
            DeviceInviteWatchState.Declined =>
                "Nothing was shared. Check you sent it to the right person.",
            DeviceInviteWatchState.Expired =>
                "Nobody finished in time, so it stopped working.",
            DeviceInviteWatchState.Cancelled =>
                "That link won't work any more.",
            _ => _isQr
                ? "Hold your phone up so they can scan it with their camera."
                // Said plainly, because the alternative is a caregiver assuming we sent it and
                // waiting for a wearer who was never contacted.
                : "The link is ready. Send it however you like — we don't send anything for you.",
        };

        StatusPlate.IsVisible = !_watch.IsFinished;
        StatusSpinner.IsRunning = !_watch.IsFinished;

        StatusLabel.Text = _watch.State == DeviceInviteWatchState.Opened
            ? "They've opened it"
            : "Waiting for them to open it";

        CountdownLabel.Text = Countdown();

        // Only an expiry is worth another go from here. A decline is an answer, and a cancel was
        // the caregiver's own doing — offering "send again" on either would read as the app
        // second-guessing a decision somebody just made.
        ResendBtn.IsVisible = _watch.State == DeviceInviteWatchState.Expired;
        ResendBtn.Text = _isQr ? "Show a new code" : "Get a new link";
        CancelLink.Text = _watch.IsFinished ? "Done" : "Cancel";
    }

    /// <summary>
    /// How long is left, in the coarsest unit that is still honest. Minutes while there are minutes,
    /// because a per-second countdown on a screen somebody is holding up for another person to scan
    /// is pressure with no purpose.
    /// </summary>
    private string Countdown()
    {
        var left = _watch.Remaining;
        if (left <= TimeSpan.Zero)
            return string.Empty;

        // Rounded up, not truncated. The remaining time is already a shade under the lifetime by
        // the time this renders, so truncating reports a freshly minted 24-hour link as 23 hours
        // and a 15-minute code as 14 — the caregiver watching the number they were just promised
        // tick down before they have done anything.
        if (left >= TimeSpan.FromHours(1))
        {
            var hours = (int)Math.Ceiling(left.TotalHours);
            return $"Works for another {hours} hour{Plural(hours)}";
        }

        if (left >= TimeSpan.FromMinutes(1))
        {
            var minutes = (int)Math.Ceiling(left.TotalMinutes);
            return $"Works for another {minutes} minute{Plural(minutes)}";
        }

        return "Expires in under a minute";
    }

    private static string Plural(int n) => n == 1 ? string.Empty : "s";

    private void StartPolling()
    {
        if (_watch.IsFinished || _polling is not null)
            return;

        _polling = new CancellationTokenSource();
        _ = PollAsync(_polling.Token);
    }

    private void StopPolling()
    {
        _polling?.Cancel();
        _polling?.Dispose();
        _polling = null;
    }

    /// <summary>
    /// Asks the server for the invitation's state until it is finished or the screen goes away.
    /// </summary>
    /// <remarks>
    /// A failed poll is not reported. The caregiver is watching somebody else's progress and can do
    /// nothing about our connectivity, so a banner appearing and vanishing as the signal comes and
    /// goes would be noise about a problem that is not theirs. The loop keeps trying, and the
    /// deadline is still honoured locally if the server never answers again.
    /// </remarks>
    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            await PollLoopAsync(ct);
        }
        finally
        {
            // The loop also ends by reaching a terminal state, not only by cancellation — and an
            // invitation that expired is exactly when the caregiver reaches for "send again". Left
            // set, this token makes StartPolling believe a poll is still running, and the
            // replacement invitation is never watched at all.
            if (!ct.IsCancellationRequested)
                StopPolling();
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_watch.IsFinished)
        {
            var delay = _watch.NextPollDelay ?? DeviceInviteWatch.IdlePollInterval;

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested)
                return;

            var changed = false;
            try
            {
                var latest = await _api.GetDeviceInviteAsync(_member.Id, _invite.InviteId, ct);
                _invite = latest;
                changed = _watch.Apply(latest.Status, latest.DeviceId);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Device invite poll failed; will retry.");
                // The server could not be reached, so the deadline is ours to enforce.
                changed = _watch.ExpireIfElapsed();
            }

            // The countdown moves whether or not the state did, so this redraws either way; the
            // flag decides only whether a terminal state needs acting on.
            await MainThread.InvokeOnMainThreadAsync(Present);

            if (changed && _watch.State == DeviceInviteWatchState.Connected)
            {
                await MainThread.InvokeOnMainThreadAsync(OnConnectedAsync);
                return;
            }
        }
    }

    /// <summary>
    /// The wearer finished. Hands over to the same confirmation the in-app flow uses, so a
    /// connection made this way lands the caregiver exactly where the other one would have.
    /// </summary>
    private async Task OnConnectedAsync()
    {
        StopPolling();
        _ctx.DeviceConnected = true;
        Preferences.Default.Remove(WizardLauncher.ResumeDismissedKey);

        DeviceResponse? device = null;
        try
        {
            var devices = await _api.GetDevicesAsync(_member.Id);
            device = devices.Devices.FirstOrDefault(d => d.DeviceId == _watch.DeviceId)
                     ?? devices.Devices.FirstOrDefault(d => d.Provider == _device.WireName);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not read the device list after a wearer connection.");
        }

        // The grant landed either way; a device list we could not read is no reason to tell the
        // caregiver otherwise, so the confirmation falls back to what we already know.
        device ??= new DeviceResponse
        {
            DeviceId = _watch.DeviceId ?? Guid.Empty,
            Provider = _device.WireName,
            DisplayName = _device.DisplayName,
            Status = "active",
        };

        // Every page that led here goes, not just this one. Two reasons, and the second is the
        // one that bites: left in the stack, the connection page is a hardware-back press away with
        // Authorize still live, which sends an already-connected member back through the provider's
        // consent screen — and Shell cannot build a route over those pages when the wizard's modal
        // is finally popped, so "Continue" on the confirmation threw and took the app down with it.
        // The in-app flow never hit this because it swaps itself out for the confirmation directly;
        // going through a waiting screen adds the page it forgot to remove.
        var spent = Navigation.NavigationStack
            .Where(page => page is InviteWaitPage or DeviceConnectionPage)
            .ToList();

        await Navigation.PushAsync(new ConnectionSuccessPage(_ctx, device));

        foreach (var page in spent)
            Navigation.RemovePage(page);
    }

    private async void OnResendClicked(object? sender, EventArgs e)
    {
        ResendBtn.IsEnabled = false;
        WaitError.IsVisible = false;

        var alive = _alive?.Token ?? CancellationToken.None;

        try
        {
            var replacement = await _api.CreateDeviceInviteAsync(_member.Id, new CreateDeviceInviteRequest
            {
                Provider = _device.WireName,
                Channel = _isQr ? "qr" : "link",
            }, alive);

            // Back or Cancel can pop this page while that was in flight. Carrying on would redraw a
            // screen nobody is looking at and start polling an invitation whose result the
            // caregiver will never see — worse, one they do not know exists.
            if (alive.IsCancellationRequested)
                return;

            _invite = replacement;
            _url = replacement.Url;
            _watch = new DeviceInviteWatch(replacement.ExpiresAt);
            QrImage.Source = null;
            Present();
            StartPolling();
        }
        catch (OperationCanceledException)
        {
            // The page went away mid-request.
        }
        catch (ApiException ex)
        {
            WaitError.Text = ex.Message;
            WaitError.IsVisible = true;
        }
        finally
        {
            ResendBtn.IsEnabled = true;
        }
    }

    private async void OnCancelTapped(object? sender, EventArgs e)
    {
        StopPolling();

        // Only a live invitation needs withdrawing. Cancelling one that already finished would be a
        // pointless round trip, and on a completed one the server rightly refuses to undo it.
        if (!_watch.IsFinished)
        {
            try
            {
                // The revoke is asked *before* the watch is marked cancelled, and its answer is
                // read. The API returns "completed" when the cancel lost the race to the wearer
                // finishing — that is the whole point of it returning the invitation's real
                // outcome — and discarding it would drop a caregiver out of a flow whose device had
                // just been connected, with nothing on screen saying so.
                var outcome = await _api.RevokeDeviceInviteAsync(_member.Id, _invite.InviteId);

                if (_watch.Apply(outcome.Status, outcome.DeviceId)
                    && _watch.State == DeviceInviteWatchState.Connected)
                {
                    await OnConnectedAsync();
                    return;
                }
            }
            catch (Exception ex)
            {
                // The link outliving this screen by its remaining minutes is a smaller problem than
                // trapping the caregiver here, and it expires on its own.
                _logger.LogInformation(ex, "Could not revoke the device invite on cancel.");
            }

            _watch.Cancel();
        }

        if (Navigation.NavigationStack.Count > 1)
            await Navigation.PopAsync();
        else
            await _ctx.CancelAsync(this);
    }

    private async void OnShareClicked(object? sender, EventArgs e)
    {
        if (_sharing)
            return;

        _sharing = true;
        try
        {
            await ShareLinkAsync(_url, _member, _device);
        }
        catch (Exception ex)
        {
            // The link is on screen and Copy still works, so a sheet that refused to open is not
            // worth an error banner over.
            _logger.LogInformation(ex, "Could not open the share sheet for a device invite.");
        }
        finally
        {
            _sharing = false;
        }
    }

    private async void OnCopyTapped(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_url))
            return;

        await Clipboard.Default.SetTextAsync(_url);

        // Confirmed on the icon that was tapped rather than in a toast: the caregiver is about to
        // paste this somewhere and needs to know it worked, without another thing to dismiss.
        CopyIcon.Source = "icon_action_check.svg";
        await Task.Delay(TimeSpan.FromSeconds(2));
        CopyIcon.Source = "icon_clipboard.svg";
    }

    /// <summary>
    /// Hands the invitation to the caregiver's own share sheet, when they ask for it.
    /// </summary>
    /// <remarks>
    /// Their sheet, their choice of app, their contact list. CardiTrack sends nothing and never
    /// learns who it went to — which is why there is no recipient field anywhere in this flow, and
    /// why this runs on a deliberate tap rather than opening itself the moment a link is minted.
    /// </remarks>
    public static Task ShareLinkAsync(
        string? url, CardiMemberResponse member, ConnectableDevice device)
    {
        var name = NameFormatting.FirstName(member.Name);

        return Share.Default.RequestAsync(new ShareTextRequest
        {
            Title = $"Connect {name}'s {device.DisplayName}",
            Subject = $"Connect your {device.DisplayName} to CardiTrack",
            Text =
                $"Hi {name} — open this to connect your {device.DisplayName} so I can keep an eye " +
                $"on how you're doing. It only works for a day.{Environment.NewLine}{url}",
        });
    }
}
