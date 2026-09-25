using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Core.Navigation;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// The medical notes kept for one CardiMember — allergies, chronic conditions, anything a
/// caregiver arriving in a hurry should have read already.
/// </summary>
/// <remarks>
/// A page rather than the drop down this used to be on Member Detail, so it reads as a peer of
/// Questions &amp; Answers: both are places the family keeps something, and both can run long
/// enough that opening them in place pushed the rest of the screen out of reach.
/// <para>
/// Not a live screen — nothing here changes unless somebody edits it — so it refetches when opened
/// and on a pull, and does not poll.
/// </para>
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(EditOnArrival), EditOnArrivalQuery)]
public partial class MedicalInformationPage : ContentPage
{
    /// <summary>Shell route; see <see cref="AppShell"/>.</summary>
    public const string Route = "medicalinformation";

    /// <summary>
    /// Query key that opens the editor as soon as the page has something to edit — what the
    /// <c>#medicalNotes</c> deep link sets, so a caregiver who tapped "Add notes" on a
    /// notification lands on somewhere to type rather than on a screen with another button.
    /// </summary>
    public const string EditOnArrivalQuery = "edit";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private readonly MemberRoute _route = new();
    private CardiMemberDetailResponse? _member;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;

    /// <summary>Set by the deep link; consumed once the first load lands.</summary>
    private bool _editOnArrival;
    private bool _isSaving;

    public MedicalInformationPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
    }

    /// <summary>
    /// Whether to open the editor on arrival. A string rather than a bool because Shell hands
    /// query values over as text; anything but "true" is read as no.
    /// </summary>
    public string EditOnArrival
    {
        set => _editOnArrival = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whose medical information this is. Shell may set this after the page has already
    /// appeared and tried to load, so an arrival that leaves a load owed runs it — see
    /// <see cref="MemberRoute"/>.
    /// </summary>
    public string MemberId
    {
        set
        {
            if (_route.Accept(value))
                this.WhenRouteHasLanded(() => _ = LoadAsync(force: true));
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async void OnBackTapped(object? sender, EventArgs e) =>
        await this.GoBackAsync($"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_route.Id}");

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync(force: true);

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync(force: true);
        Refresher.IsRefreshing = false;
    }

    private async void OnEditTapped(object? sender, TappedEventArgs e) => await OpenEditorAsync();

    private async void OnEditClicked(object? sender, EventArgs e) => await OpenEditorAsync();

    private async void OnStillAccurateClicked(object? sender, EventArgs e) => await ConfirmAsync();

    /// <summary>
    /// Opens the notes in their own form and saves what comes back.
    /// </summary>
    /// <remarks>
    /// A popup rather than the trip to M1-14 this used to make. That screen is the whole profile —
    /// name, date of birth, sex, relationship, photo — and every one of those is a field a
    /// caregiver can disturb on the way to the one they came for. It also makes the asking
    /// repeatable, which is the point of dating the background: a notification saying it has been
    /// six months can put this in front of somebody in one tap.
    /// </remarks>
    private async Task OpenEditorAsync()
    {
        if (_member is null || _isSaving)
            return;

        var edited = await _popups.EditMedicalNotesAsync(
            _member.DisplayFirstName(), _member.MedicalNotes);

        // Null is "cancelled"; an empty string is a background the caregiver deliberately cleared.
        if (edited is null)
            return;

        // Unchanged text is not a save. The server would decline to re-date it anyway — only a
        // real change moves the review date — so a request here would be a round trip that
        // reports nothing, and a "Still accurate" tap is how somebody says this on purpose.
        if (string.Equals(edited, _member.MedicalNotes?.Trim() ?? string.Empty, StringComparison.Ordinal))
            return;

        await SaveAsync(
            member => _api.UpdateCardiMemberAsync(member.Id, RequestFor(member, edited)),
            "Couldn't save these notes");
    }

    /// <summary>
    /// Records that the notes were read and found still current, without changing them — the
    /// confirmation the edit form cannot express, since it carries the notes on every save whether
    /// or not anybody looked at them.
    /// </summary>
    private Task ConfirmAsync() =>
        SaveAsync(
            member => _api.ConfirmMedicalNotesAsync(member.Id),
            "Couldn't confirm these notes");

    /// <summary>
    /// Runs a write against the member and repaints from what the server stored, so the review
    /// date on screen is the one on file rather than one this screen guessed at.
    /// </summary>
    private async Task SaveAsync(
        Func<CardiMemberDetailResponse, Task<CardiMemberDetailResponse>> write, string errorTitle)
    {
        if (_member is null || _isSaving)
            return;

        _isSaving = true;
        try
        {
            _member = await write(_member);
            Apply(_member);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(
                ex.Errors is { Count: > 0 } ? string.Join('\n', ex.Errors) : ex.Message, errorTitle);
        }
        catch (ApiException)
        {
            // Session gone — the app is already on its way back to sign-in.
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>
    /// The edit form is a full replacement, so everything this screen did not ask about is echoed
    /// back from the copy it holds — an omitted field is a cleared one. Sex and the photo are the
    /// two that mean "leave it alone" when omitted, and they are omitted for exactly that reason:
    /// a form that never showed them must not be the thing that restates them.
    /// </summary>
    private static UpdateCardiMemberRequest RequestFor(CardiMemberDetailResponse member, string notes) =>
        new()
        {
            FirstName = member.FirstName,
            LastName = member.LastName,
            // Restated for an API from before the first/last split, which reads only this; a
            // current API ignores it whenever FirstName is sent.
            Name = member.Name,
            DateOfBirth = member.DateOfBirth,
            RelationshipType = member.Relationship,
            Email = member.Email,
            Phone = member.Phone,
            EmergencyContactName = member.EmergencyContactName,
            EmergencyContactPhone = member.EmergencyContactPhone,
            MedicalNotes = string.IsNullOrWhiteSpace(notes) ? null : notes,
            AlertSensitivity = member.AlertSensitivity,
        };

    /// <param name="force">
    /// Supersedes a load already in flight rather than skipping — for anything the caregiver
    /// asked for by hand. A gesture that did nothing because a slow request happened to be
    /// running is a gesture they will make again.
    /// </param>
    private async Task LoadAsync(bool force = false)
    {
        if (_gate.IsLoading && !force)
            return;
        // Nothing to ask about yet. The empty id is not a member the API can refuse politely —
        // every CardiMember endpoint answers it with "CardiMember not found", which reads as
        // this member being gone — so the request is not made at all, and the arrival that
        // brings the id runs this again.
        if (_route.IsMissing)
        {
            _route.LoadedWithoutId();
            ErrorDetailLabel.Text = MemberRoute.MissingMessage;
            SetState(error: true);
            return;
        }

        var ticket = _gate.Begin();
        var memberId = _route.Id;

        if (_member is null)
            SetState(loading: true);

        try
        {
            // The saved profile first on a landing with nothing on screen, the live one behind
            // it. Notes rarely change, so identical notes are left alone rather than flashed.
            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: _member is null ? ct => _api.PeekCardiMemberAsync(memberId, ct) : null,
                fetch: ct => _api.GetCardiMemberAsync(memberId, ct),
                render: member =>
                {
                    _member = member;
                    Apply(member);
                    SetState(loaded: true);
                },
                _feedback,
                // What Apply draws, and only that: a member payload changes whenever a sync
                // lands, and notes that have not moved must not flash "Updating…" for it.
                // Compared whole rather than field by field, so nothing inside these can slip past.
                sameAs: (a, b) => SamePayload.Same(
                    new { a.Name, a.MedicalNotes, a.IsPrimaryCaregiver, a.MedicalNotesReviewedAtUtc },
                    new { b.Name, b.MedicalNotes, b.IsPrimaryCaregiver, b.MedicalNotesReviewedAtUtc }));

            // Keep whatever is already on screen — a failed refresh must not blank notes somebody
            // may be reading (the banner says they are saved) — and only offer the error when
            // there is nothing behind it, or when the member is gone.
            if (outcome.Result == RefreshResult.NothingAndFailed)
            {
                _member = null;
                ErrorDetailLabel.Text = outcome.Error!.Message;
                SetState(error: true);
            }
        }
        catch (Exception ex)
        {
            // Anything that is not the API answering badly — a fault while putting the notes on
            // screen. Without this it is silent and permanent: the callers are an async void pull
            // handler and a fire-and-forget OnAppearing, so nothing observes the throw, the page
            // never leaves its skeleton, and every retry meets the same data and fails the same
            // way. The same hole was fixed on DashboardPage in this branch; this page inherited it
            // by being written from the same shape.
            ScreenRefresh.LogFailure(ex, this, "while loading");
            if (_member is null)
            {
                ErrorDetailLabel.Text = "Something went wrong while showing this.";
                SetState(error: true);
            }
        }
        finally
        {
            _gate.Release(ticket);
        }
    }

    private void Apply(CardiMemberDetailResponse member)
    {
        var hasNotes = !string.IsNullOrWhiteSpace(member.MedicalNotes);
        var firstName = member.DisplayFirstName();
        ChatBot.MemberId = _route.Id;
        ChatBot.MemberFirstName = firstName;

        NotesLabel.Text = hasNotes
            ? member.MedicalNotes
            : $"Nothing recorded for {firstName} yet. Allergies, chronic conditions and anything a "
              + "caregiver should know before they arrive belong here.";

        // The pencil is for changing notes that exist; the button below is for starting them. Only
        // one of the two is ever offered, and only to the caregiver allowed to act on it.
        EditButton.IsVisible = member.IsPrimaryCaregiver && hasNotes;
        AddNotesButton.IsVisible = member.IsPrimaryCaregiver && !hasNotes;

        // Confirming is only meaningful once there is something on file to confirm.
        StillAccurateButton.IsVisible = member.IsPrimaryCaregiver && hasNotes;

        ReviewedLabel.IsVisible = hasNotes;
        ReviewedLabel.Text = ReviewedLine(member.MedicalNotesReviewedAtUtc);

        // The deep link's request, honoured once rather than on every repaint — a save calls
        // Apply again, and reopening the editor on top of the save that just closed it would trap
        // whoever tapped the notification.
        if (_editOnArrival)
        {
            _editOnArrival = false;
            if (member.IsPrimaryCaregiver)
                _ = OpenEditorAsync();
        }
    }

    /// <summary>
    /// When somebody last said the background is still true, in both forms a caregiver might want:
    /// the date, for the record, and how long ago that was, which is the part that tells them
    /// whether to look.
    /// </summary>
    /// <remarks>
    /// A null says so plainly rather than falling back to the member's created or updated date.
    /// Neither is evidence anybody read the notes — updated moves on any profile edit — and a
    /// date that implies a review nobody did is worse than admitting there has not been one.
    /// </remarks>
    private static string ReviewedLine(DateTime? reviewedAtUtc)
    {
        if (reviewedAtUtc is not { } reviewed)
            return "Not confirmed yet — nobody has said whether this is still current.";

        var local = DateTime.SpecifyKind(reviewed, DateTimeKind.Utc).ToLocalTime();
        return $"Confirmed {local:d MMM yyyy} · {RelativeTime.Format(reviewed)}";
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
        Refresher.IsVisible = loaded;
    }
}
