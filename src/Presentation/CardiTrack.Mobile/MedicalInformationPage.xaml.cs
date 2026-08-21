using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
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
public partial class MedicalInformationPage : ContentPage
{
    /// <summary>Shell route; see <see cref="AppShell"/>.</summary>
    public const string Route = "medicalinformation";

    private readonly ICardiTrackApiClient _api;

    private Guid _memberId;
    private CardiMemberDetailResponse? _member;
    private bool _isLoading;

    public MedicalInformationPage(ICardiTrackApiClient api)
    {
        InitializeComponent();
        _api = api;
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async void OnBackTapped(object? sender, EventArgs e) =>
        await this.GoBackAsync($"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_memberId}");

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private async void OnEditTapped(object? sender, TappedEventArgs e) => await OpenEditorAsync();

    private async void OnEditClicked(object? sender, EventArgs e) => await OpenEditorAsync();

    /// <summary>
    /// Opens the profile form on the medical notes rather than at the top of it, the same way the
    /// phone card on Member Detail opens it on the number.
    /// </summary>
    private Task OpenEditorAsync() =>
        Shell.Current.GoToAsync(
            $"{EditCardiMemberPage.Route}?memberId={_memberId}&focus={Uri.EscapeDataString(EditCardiMemberPage.FocusMedical)}");

    private async Task LoadAsync()
    {
        if (_isLoading)
            return;
        _isLoading = true;

        if (_member is null)
            SetState(loading: true);

        try
        {
            _member = await _api.GetCardiMemberAsync(_memberId);
            Apply(_member);
            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            // Keep whatever is already on screen — a failed refresh must not blank notes somebody
            // may be reading — and only offer the error when there is nothing behind it.
            if (_member is null)
            {
                ErrorDetailLabel.Text = ex.Message;
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
            _isLoading = false;
        }
    }

    private void Apply(CardiMemberDetailResponse member)
    {
        var hasNotes = !string.IsNullOrWhiteSpace(member.MedicalNotes);
        var firstName = NameFormatting.FirstName(member.Name);
        ChatBot.MemberId = _memberId;
        ChatBot.MemberFirstName = firstName;

        NotesLabel.Text = hasNotes
            ? member.MedicalNotes
            : $"Nothing recorded for {firstName} yet. Allergies, chronic conditions and anything a "
              + "caregiver should know before they arrive belong here.";

        // The pencil is for changing notes that exist; the button below is for starting them. Only
        // one of the two is ever offered, and only to the caregiver allowed to act on it.
        EditButton.IsVisible = member.IsPrimaryCaregiver && hasNotes;
        AddNotesButton.IsVisible = member.IsPrimaryCaregiver && !hasNotes;
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
        Refresher.IsVisible = loaded;
    }
}
