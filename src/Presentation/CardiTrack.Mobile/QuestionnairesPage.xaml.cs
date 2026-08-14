using System.Collections.ObjectModel;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// Everything the service has asked this member's family, and what they answered — with the answers
/// editable and removable, searchable by question or answer text, and lazily loaded as the family
/// scrolls back through it.
/// </summary>
/// <remarks>
/// An archive rather than a live screen, so it refetches when opened and on a pull, and does not
/// poll: nothing here changes unless the person looking at it changes it.
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(MemberName), "name")]
public partial class QuestionnairesPage : ContentPage
{
    /// <summary>Shell route; see <see cref="AppShell"/>.</summary>
    public const string Route = "memberquestions";

    /// <summary>How many answered questions a page fetches. Small enough that a page arrives while
    /// a scroll is still in motion, large enough that scrolling rarely outruns it.</summary>
    private const int PageSize = 20;

    /// <summary>How long to hold after a keystroke before searching — long enough that a caregiver
    /// typing a whole word does not fire a request per letter, short enough to still feel live.</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(350);

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly ObservableCollection<AnsweredQuestionnaireItem> _answeredItems = [];

    private Guid _memberId;
    private string? _memberName;
    private string? _searchTerm;
    private int _currentPage;
    private bool _hasMorePages;
    private bool _isLoadingMore;
    private bool _isBusy;
    private CancellationTokenSource? _searchDebounceCts;

    public QuestionnairesPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;

        AnsweredList.ItemsSource = _answeredItems;
        PendingCard.AnswerSubmitted += OnPendingAnswered;
        PendingCard.DismissRequested += OnPendingDismissed;
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    /// <summary>
    /// Passed through from the page that opened this one rather than refetched. The name is only
    /// wanted for the copy, and the caller already has it — a second round trip to address someone
    /// by name would be a slower screen for nothing.
    /// </summary>
    public string MemberName
    {
        set => _memberName = Uri.UnescapeDataString(value ?? string.Empty);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnBackTapped(object? sender, EventArgs e) =>
        await this.GoBackAsync($"{CardiMemberDetailPage.Route}?memberId={_memberId}");

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ClearSearchButton.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);
        _searchTerm = string.IsNullOrWhiteSpace(e.NewTextValue) ? null : e.NewTextValue.Trim();

        _searchDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        _ = DebouncedSearchAsync(cts.Token);
    }

    private void OnClearSearchClicked(object? sender, EventArgs e) => SearchEntry.Text = string.Empty;

    private async Task DebouncedSearchAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounce, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested)
            await LoadAsync();
    }

    /// <summary>(Re)loads from page one — the initial open, a pull-to-refresh, a search edit, or
    /// anything that changes what belongs at the top of the list.</summary>
    private async Task LoadAsync()
    {
        SetState(loading: true);
        _currentPage = 1;

        try
        {
            var result = await _api.GetQuestionnairesAsync(_memberId, _searchTerm, _currentPage, PageSize);
            ApplyHeader(result);

            _answeredItems.Clear();
            foreach (var questionnaire in result.Answered.Items)
                _answeredItems.Add(new AnsweredQuestionnaireItem(questionnaire, _memberName));

            _hasMorePages = result.Answered.HasMore;
            EmptyPanel.IsVisible = result.Answered.TotalCount == 0;
            if (EmptyPanel.IsVisible)
                ApplyEmptyStateText();

            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            ErrorDetailLabel.Text = ex.Message;
            SetState(error: true);
        }
    }

    /// <summary>Fetches the next page and appends it — never resets what is already on screen, so a
    /// caregiver mid-scroll never loses their place.</summary>
    private async Task LoadMoreAsync()
    {
        if (_isLoadingMore || !_hasMorePages)
            return;

        _isLoadingMore = true;
        LoadMoreSkeleton.IsVisible = true;
        try
        {
            var nextPage = _currentPage + 1;
            var result = await _api.GetQuestionnairesAsync(_memberId, _searchTerm, nextPage, PageSize);

            foreach (var questionnaire in result.Answered.Items)
                _answeredItems.Add(new AnsweredQuestionnaireItem(questionnaire, _memberName));

            _currentPage = nextPage;
            _hasMorePages = result.Answered.HasMore;
        }
        catch (ApiException)
        {
            // Best-effort: staying on what already loaded beats an error banner over a scroll
            // gesture. The next pull-to-refresh or search edit gets another chance.
            _hasMorePages = false;
        }
        finally
        {
            _isLoadingMore = false;
            LoadMoreSkeleton.IsVisible = false;
        }
    }

    private void OnRemainingItemsThresholdReached(object? sender, EventArgs e) => _ = LoadMoreAsync();

    private void ApplyHeader(QuestionnairesPageResponse result)
    {
        var name = string.IsNullOrWhiteSpace(_memberName) ? "them" : _memberName;
        IntroLabel.Text =
            $"Things we've asked about {name}, and what you told us. Your answers help us read "
            + "their readings properly.";

        PendingCard.IsVisible = result.Pending is not null;
        if (result.Pending is not null)
            PendingCard.Apply(result.Pending, _memberName);

        // Nothing to search until there is a first answer to find.
        SearchBorder.IsVisible = result.HasAny;
    }

    private void ApplyEmptyStateText()
    {
        var name = string.IsNullOrWhiteSpace(_memberName) ? "them" : _memberName;

        if (!string.IsNullOrWhiteSpace(_searchTerm))
        {
            EmptyTitleLabel.Text = "No matches";
            EmptyDetailLabel.Text =
                $"Nothing about “{_searchTerm}” yet — try a different word, or clear the "
                + "search to see everything.";
        }
        else
        {
            EmptyTitleLabel.Text = "Nothing to look back on yet";
            EmptyDetailLabel.Text = $"When we ask something about {name} and you answer, we'll keep it here.";
        }
    }

    private async void OnPendingAnswered(object? sender, string answer)
    {
        if (_isBusy || PendingCard.Questionnaire is not { } questionnaire)
            return;

        _isBusy = true;
        PendingCard.SetBusy(true);
        try
        {
            await _api.AnswerQuestionnaireAsync(
                questionnaire.Id, new AnswerQuestionnaireRequest { AnswerText = answer });

            // A full reload rather than a patch: answering the pending question both removes it
            // from the header and inserts a brand-new row at the top of the answered list.
            PendingCard.CloseEditor();
            await LoadAsync();
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't save your answer");
        }
        finally
        {
            _isBusy = false;
            PendingCard.SetBusy(false);
        }
    }

    private async void OnPendingDismissed(object? sender, EventArgs e)
    {
        if (PendingCard.Questionnaire is not { } questionnaire || _isBusy)
            return;

        var confirmed = await _popups.ConfirmInfoAsync(
            "We won't ask this one again.", "Skip this question?", "Yes, skip", "Keep it");
        if (!confirmed)
            return;

        _isBusy = true;
        PendingCard.SetBusy(true);
        try
        {
            await _api.DismissQuestionnaireAsync(questionnaire.Id);
            await LoadAsync();
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't skip that question");
        }
        finally
        {
            _isBusy = false;
            PendingCard.SetBusy(false);
        }
    }

    /// <summary>
    /// An edit to an already-answered question. Patched into <see cref="_answeredItems"/> in place
    /// rather than reloaded — this list can now be many pages deep, and a reload would both refetch
    /// pages already on screen and throw the scroll position away.
    /// </summary>
    private async void OnAnsweredRowAnswerSubmitted(
        object? sender, (QuestionnaireResponse Questionnaire, string Answer) e)
    {
        if (_isBusy || sender is not AnsweredQuestionRow row)
            return;

        _isBusy = true;
        row.SetBusy(true);
        try
        {
            var updated = await _api.AnswerQuestionnaireAsync(
                e.Questionnaire.Id, new AnswerQuestionnaireRequest { AnswerText = e.Answer });

            row.CloseEditor();
            ReplaceAnsweredItem(updated);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't save your answer");
        }
        finally
        {
            _isBusy = false;
            row.SetBusy(false);
        }
    }

    private async void OnAnsweredRowDeleteRequested(object? sender, QuestionnaireResponse questionnaire)
    {
        if (_isBusy)
            return;

        // There is no undo, and the answer is gone for the whole family — the same confirmation
        // weight deleting an alert carries.
        var confirmed = await _popups.ConfirmWarningAsync(
            "This question and your answer will be removed for everyone.",
            "Delete this answer?",
            "Yes, delete");
        if (!confirmed)
            return;

        _isBusy = true;
        try
        {
            await _api.DeleteQuestionnaireAsync(questionnaire.Id);
            RemoveAnsweredItem(questionnaire.Id);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't delete that answer");
        }
        finally
        {
            _isBusy = false;
        }
    }

    /// <summary>
    /// Re-stamped answers sort back to the top — the same order <c>QuestionnaireService.AnswerAsync</c>
    /// gives the server's copy of the list.
    /// </summary>
    private void ReplaceAnsweredItem(QuestionnaireResponse updated)
    {
        var index = IndexOfAnswered(updated.Id);
        if (index < 0)
            return;

        _answeredItems.RemoveAt(index);
        _answeredItems.Insert(0, new AnsweredQuestionnaireItem(updated, _memberName));
    }

    private void RemoveAnsweredItem(Guid questionnaireId)
    {
        var index = IndexOfAnswered(questionnaireId);
        if (index < 0)
            return;

        _answeredItems.RemoveAt(index);

        if (_answeredItems.Count > 0 || _hasMorePages)
        {
            EmptyPanel.IsVisible = false;
            return;
        }

        EmptyPanel.IsVisible = true;
        ApplyEmptyStateText();
    }

    private int IndexOfAnswered(Guid questionnaireId)
    {
        for (var i = 0; i < _answeredItems.Count; i++)
        {
            if (_answeredItems[i].Questionnaire.Id == questionnaireId)
                return i;
        }

        return -1;
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        Refresher.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }
}
