using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile;

/// <summary>
/// M1-17 Health Data Export (Story 6.3). Entered from M1-13 CardiMember Detail and from Settings.
/// </summary>
/// <remarks>
/// <para>
/// The four Figma states are four panels on one page rather than four screens: the caregiver who
/// hits an error should land back on the form they filled in, not at the start of a flow.
/// </para>
/// <para>
/// Export is open to every account: nothing in CardiTrack is gated by plan today, so the page asks
/// only for the member list before showing the form.
/// </para>
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
public partial class ExportHealthDataPage : ContentPage
{
    public const string Route = "exporthealthdata";

    /// <summary>
    /// How long to keep polling before calling it lost. Generously past any real generation —
    /// the AI narrative in a PDF is the slow part — so the ceiling only catches a report the
    /// server has quietly stopped working on.
    /// </summary>
    private static readonly TimeSpan GenerationCeiling = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Matches the API's own cap (<c>GenerateReportValidator.MaxRangeDays</c>).</summary>
    private const int MaxRangeDays = 365;

    /// <summary>The three MVP 1 formats, in the order M1-17 lists them.</summary>
    private static readonly (ReportFormat Format, string Name, string Detail)[] Formats =
    [
        (ReportFormat.Pdf, "PDF report",
            "A readable summary with tables — for family, or to print for an appointment."),
        (ReportFormat.Csv, "CSV spreadsheet",
            "The raw daily numbers, to open in Excel or Numbers."),
        (ReportFormat.FhirR4, "FHIR R4",
            "Accepted by most US patient portals and EHR systems — for a doctor's office. "
            + "Carries readings and devices; alerts are in the PDF and CSV."),
    ];

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly IExportConsentFlow _consent;
    private readonly Dictionary<ReportFormat, Border> _formatCards = [];

    private Guid _memberId;
    private List<CardiMemberResponse> _members = [];
    private ReportFormat _selectedFormat = ReportFormat.Pdf;
    private CancellationTokenSource? _generation;
    private CancellationTokenSource? _page;
    private ReportFile? _ready;
    private string? _readyPath;
    private bool _isLoading;
    private bool _exporting;

    public ExportHealthDataPage(
        ICardiTrackApiClient api,
        IPopupService popups,
        IExportConsentFlow consent)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        _consent = consent;

        BuildFormatCards();
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

        // A popup closing raises OnAppearing again; reloading then would throw away a form the
        // caregiver is part-way through filling in. Returning from OS Settings during enrollment
        // does the same while _exporting is still true — LoadAsync would reset the dates and
        // toggles the consent token was fingerprinted against.
        if (_popups.IsShowing)
            return;

        // Consent/enrollment: keep the form. A cancelled generation must reload —
        // returning before finally runs would otherwise leave GeneratingPanel up.
        if (_exporting && _generation is not { IsCancellationRequested: true })
            return;

        // Nor reload on the way back from the share sheet — the finished export is the point.
        if (_ready is not null)
            return;

        _ = LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // Consent popups (and the password sheet) are PushModalAsync. That disappears this
        // page without the caregiver leaving it — same handshake as OnAppearing. Cancelling
        // the generation token then would abort the export they just confirmed.
        if (_popups.IsShowing)
            return;

        // Leaving the page abandons the poll. The report still finishes server-side; there is
        // just no longer anyone here to hand it to.
        _generation?.Cancel();

        // Page-lifetime token is for the consent HTTP waits, not for popup/native UI.
        // Native biometric also disappears this page while it is still CurrentPage.
        if (!ScreenRefresh.IsOnScreen(this))
            _page?.Cancel();
    }

    // ── Loading ─────────────────────────────────────────────────────────────────

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_isLoading)
            return;
        _isLoading = true;

        ShowOnly(SkeletonPanel);

        // Anything left in the cache by a previous visit — the caregiver hit back, or the OS
        // killed the app, on a path no explicit cleanup can cover. Swept on arrival rather than
        // on the way out, because leaving is also what happens when the share sheet opens.
        DiscardCachedExports();

        try
        {
            _members = (await _api.GetCardiMembersAsync()).ToList();
            if (_members.Count == 0)
            {
                ErrorDetailLabel.Text = "There's nobody to export data for yet.";
                ShowOnly(ErrorPanel);
                return;
            }

            PopulateForm();
            ShowOnly(FormPanel);
        }
        catch (ApiException ex)
        {
            ErrorDetailLabel.Text = ex.Message;
            ShowOnly(ErrorPanel);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void PopulateForm()
    {
        MemberPicker.ItemsSource = _members.Select(m => m.Name).ToList();

        // Whoever the caregiver came from, if they came from a member's detail page.
        var index = _members.FindIndex(m => m.Id == _memberId);
        MemberPicker.SelectedIndex = index >= 0 ? index : 0;

        HeaderSubtitleLabel.Text = "For a doctor's visit, or your own records";

        var today = DateTime.Today;
        ToPicker.Date = today;
        FromPicker.Date = today.AddDays(-29);
        FromPicker.MaximumDate = today;
        ToPicker.MaximumDate = today;

        SelectFormat(ReportFormat.Pdf);
        UpdateEstimate();
    }

    private void BuildFormatCards()
    {
        foreach (var (format, name, detail) in Formats)
        {
            var card = new Border
            {
                StrokeThickness = 1,
                Padding = new Thickness(12),
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Content = new VerticalStackLayout
                {
                    Spacing = 2,
                    Children =
                    {
                        new Label { Text = name, Style = (Style)Resources["FormatNameStyle"] },
                        new Label { Text = detail, Style = (Style)Resources["FormatDetailStyle"] },
                    }
                }
            };

            card.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => SelectFormat(format))
            });

            _formatCards[format] = card;
            FormatStack.Add(card);
        }
    }

    private void SelectFormat(ReportFormat format)
    {
        _selectedFormat = format;

        foreach (var (candidate, card) in _formatCards)
        {
            var selected = candidate == format;
            card.BackgroundColor = selected
                ? (Color)Microsoft.Maui.Controls.Application.Current!.Resources["SelectedOptionBackground"]
                : (Color)Microsoft.Maui.Controls.Application.Current!.Resources["White"];
            card.Stroke = selected
                ? (Color)Microsoft.Maui.Controls.Application.Current!.Resources["Primary"]
                : (Color)Microsoft.Maui.Controls.Application.Current!.Resources["Divider"];
        }

        UpdateEstimate();
    }

    // ── Form interaction ────────────────────────────────────────────────────────

    private void OnPresetClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string parameter }
            || !int.TryParse(parameter, out var days))
            return;

        ToPicker.Date = DateTime.Today;
        FromPicker.Date = DateTime.Today.AddDays(-(days - 1));
        UpdateEstimate();
    }

    private void OnDateChanged(object? sender, DateChangedEventArgs e) => UpdateEstimate();

    private void OnSelectionChanged(object? sender, CheckedChangedEventArgs e) => UpdateEstimate();

    /// <summary>
    /// Keeps the hint, the size estimate and the button's enabled state in step with the form —
    /// the same three rules the API enforces, said before the request rather than after it.
    /// </summary>
    private void UpdateEstimate()
    {
        var days = (SelectedTo - SelectedFrom).Days + 1;
        var anySection = MetricsCheck.IsChecked || AlertsCheck.IsChecked || DevicesCheck.IsChecked
                         || JournalsCheck.IsChecked || NoticesCheck.IsChecked;

        if (days <= 0)
        {
            RangeHintLabel.Text = "The end date needs to be on or after the start date.";
            EstimateLabel.Text = string.Empty;
            ExportButton.IsEnabled = false;
            return;
        }

        if (days > MaxRangeDays)
        {
            RangeHintLabel.Text = $"Choose a period of up to {MaxRangeDays} days.";
            EstimateLabel.Text = string.Empty;
            ExportButton.IsEnabled = false;
            return;
        }

        RangeHintLabel.Text = $"{days} day{(days == 1 ? string.Empty : "s")}";

        if (!anySection)
        {
            EstimateLabel.Text = "Choose at least one kind of data to include.";
            ExportButton.IsEnabled = false;
            return;
        }

        // The API refuses this too; saying so here means the caregiver finds out while they can
        // still fix it, rather than after tapping Export.
        if (_selectedFormat == ReportFormat.FhirR4 && !MetricsCheck.IsChecked && !DevicesCheck.IsChecked)
        {
            EstimateLabel.Text =
                "FHIR R4 carries readings and devices — tick one of those, or choose PDF or CSV "
                + "to export alerts.";
            ExportButton.IsEnabled = false;
            return;
        }

        EstimateLabel.Text = $"Estimated size: {ExportSizeEstimate.Describe(days, _selectedFormat)}";
        ExportButton.IsEnabled = true;
    }

    // ── Generating ──────────────────────────────────────────────────────────────

    private async void OnExportClicked(object? sender, EventArgs e)
    {
        if (_exporting)
            return;

        var member = SelectedMember();
        if (member is null)
            return;

        _exporting = true;
        ExportButton.IsEnabled = false;
        _generation?.Cancel();
        _generation = null;
        _page?.Cancel();
        _page = new CancellationTokenSource();
        var pageCt = _page.Token;
        try
        {
            // Snapshot first: OnAppearing after Settings must not rebuild generate from
            // reset controls. The generation CTS starts after confirmation — creating it
            // first meant every consent modal's OnDisappearing cancelled the export.
            var snapshot = BuildRequest(member, consentToken: "");
            var consent = await ConfirmExportAsync(snapshot, pageCt);
            if (consent is null)
                return;
            if (!ScreenRefresh.IsOnScreen(this))
                return;

            _generation = new CancellationTokenSource();
            var ct = _generation.Token;

            GeneratingDetailLabel.Text = consent.Reused
                ? "Using your earlier confirmation — we're preparing the copy."
                : _selectedFormat == ReportFormat.Pdf
                    ? "We're writing the summary — this usually takes under a minute."
                    : "This usually takes a few seconds.";
            ShowOnly(GeneratingPanel);

            try
            {
                var queued = await _api.GenerateReportAsync(
                    WithConsentToken(snapshot, consent.Token), ct);

                var status = await PollUntilReadyAsync(queued.ReportId, ct);

                if (status is null || status.Status != ReportStatus.Ready)
                {
                    FailedDetailLabel.Text = status?.Error
                        ?? "We couldn't finish that export. Please try again.";
                    ShowOnly(FailedPanel);
                    return;
                }

                _ready = await _api.DownloadReportAsync(queued.ReportId, ct);
                _readyPath = await WriteToCacheAsync(_ready, ct);

                CompleteDetailLabel.Text =
                    $"{_ready.FileName} · {Describe(_ready.Content.LongLength)}";
                ShowOnly(CompletePanel);
            }
            catch (OperationCanceledException)
            {
                // The caregiver cancelled, or left the page. Either way there is nothing to say.
                if (!ct.IsCancellationRequested)
                    throw;
            }
            catch (ApiException ex)
            {
                FailedDetailLabel.Text = ex.Message;
                ShowOnly(FailedPanel);
            }
        }
        finally
        {
            _exporting = false;
            if (FormPanel.IsVisible)
                UpdateEstimate();
        }
    }

    /// <summary>
    /// Polls until the report reaches a terminal state, or the ceiling passes.
    /// </summary>
    /// <remarks>
    /// A missing report (null) is treated as still-pending rather than as failure: the row is
    /// written before the 202 is returned, so a null here is a hiccup, and giving up on it would
    /// throw away a report that is about to be ready.
    /// </remarks>
    private async Task<ReportStatusResponse?> PollUntilReadyAsync(string reportId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + GenerationCeiling;
        ReportStatusResponse? last = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            last = await _api.GetReportStatusAsync(reportId, ct);
            if (last is not null && last.Status != ReportStatus.Pending)
                return last;

            await Task.Delay(PollInterval, ct);
        }

        return last;
    }

    private void OnCancelGenerationClicked(object? sender, EventArgs e)
    {
        _generation?.Cancel();
        ShowOnly(FormPanel);
        UpdateEstimate();
    }

    // ── Delivery ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the export to the app's cache so the OS can hand it to another app by path. Cache
    /// rather than a permanent directory: once it is shared or saved, the copy the caregiver
    /// keeps is the one they chose, and a second copy of a health record sitting in app storage
    /// is a liability with no reader.
    /// </summary>
    private static async Task<string> WriteToCacheAsync(ReportFile file, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(FileSystem.CacheDirectory, file.FileName);
        await File.WriteAllBytesAsync(path, file.Content, ct);
        return path;
    }

    /// <summary>
    /// The share sheet is both delivery methods M1-17 asks for: on iOS and Android it is the
    /// route to "Save to Files" / "Save to Drive" as well as to mail and messaging. A separate
    /// "Save to Device" button would open this same sheet, so there is one action rather than two
    /// that do the same thing wearing different labels.
    /// </summary>
    private async void OnShareClicked(object? sender, EventArgs e)
    {
        if (_ready is null || _readyPath is null)
            return;

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Save or share export",
            File = new ShareFile(_readyPath)
        });
    }

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        if (_readyPath is null)
            return;

        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                Title = _ready?.FileName,
                File = new ReadOnlyFile(_readyPath)
            });
        }
        catch (Exception)
        {
            // No installed app claims the type — likelier for FHIR JSON than for a PDF.
            await _popups.ShowInfoAsync(
                "There's no app on this device that opens this kind of file. Try \"Save or share\" instead.",
                "Can't open it here");
        }
    }

    private void OnStartOverClicked(object? sender, EventArgs e)
    {
        // Delete, not just dereference. The comment on WriteToCacheAsync calls this copy a
        // liability with no reader, and the caregiver asking for a different export is the one
        // moment we know for certain they are finished with this one.
        DiscardCachedExports();

        _ready = null;
        _readyPath = null;
        ShowOnly(FormPanel);
        UpdateEstimate();
    }

    /// <summary>
    /// Removes every export this screen has left in the app cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not called from <c>OnDisappearing</c>: opening the share sheet disappears this
    /// page too, and deleting the file there would pull it out from under the app the caregiver
    /// just chose to send it to. Sweeping on arrival instead covers every way of leaving —
    /// including the ones no handler sees — at the cost of the file surviving until the next
    /// visit, which is the trade an unkillable cleanup path cannot avoid.
    /// </para>
    /// <para>
    /// Scoped to the export naming scheme, so nothing else in the cache is this method's to
    /// delete. Failures are swallowed: a file the OS has locked or already evicted is not
    /// something to fail a screen over, and the next sweep retries it.
    /// </para>
    /// </remarks>
    private static void DiscardCachedExports()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         FileSystem.CacheDirectory, "carditrack-export-*"))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    // One undeletable file must not stop the sweep clearing the rest.
                }
            }
        }
        catch (Exception)
        {
            // No cache directory yet, or it is unreadable — nothing to clean either way.
        }
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Responsibility, how long to keep it, then password or fingerprint / face
    /// unlock — or a standing grant reused with the caregiver told so.
    /// </summary>
    private async Task<ExportConsentOutcome?> ConfirmExportAsync(
        GenerateReportRequest snapshot, CancellationToken ct)
    {
        try
        {
            return await _consent.ConfirmAsync(snapshot, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static GenerateReportRequest WithConsentToken(
        GenerateReportRequest snapshot, string consentToken) => new()
    {
        CardiMemberIds = snapshot.CardiMemberIds,
        DateRangeFrom = snapshot.DateRangeFrom,
        DateRangeTo = snapshot.DateRangeTo,
        Format = snapshot.Format,
        IncludeMetrics = snapshot.IncludeMetrics,
        IncludeTrends = snapshot.IncludeTrends,
        IncludeAlerts = snapshot.IncludeAlerts,
        IncludeJournals = snapshot.IncludeJournals,
        IncludeNotices = snapshot.IncludeNotices,
        IncludeDevices = snapshot.IncludeDevices,
        JournalEntryDate = snapshot.JournalEntryDate,
        JournalAudience = snapshot.JournalAudience,
        ConsentToken = consentToken,
        Title = snapshot.Title
    };

    private GenerateReportRequest BuildRequest(CardiMemberResponse member, string consentToken) => new()
    {
        CardiMemberIds = [member.Id],
        DateRangeFrom = DateOnly.FromDateTime(SelectedFrom),
        DateRangeTo = DateOnly.FromDateTime(SelectedTo),
        Format = _selectedFormat,
        IncludeMetrics = MetricsCheck.IsChecked,
        IncludeTrends = TrendsCheck.IsChecked,
        IncludeAlerts = AlertsCheck.IsChecked,
        IncludeJournals = JournalsCheck.IsChecked,
        IncludeNotices = NoticesCheck.IsChecked,
        IncludeDevices = DevicesCheck.IsChecked,
        ConsentToken = consentToken,
        Title = $"{member.Name} — health export"
    };

    /// <summary>
    /// The pickers' dates, which the control exposes as nullable. Both are set in
    /// <see cref="PopulateForm"/> before the form is ever shown, so an unset value would mean the
    /// form was driven before it was populated — today is the harmless reading of that.
    /// </summary>
    private DateTime SelectedFrom => FromPicker.Date ?? DateTime.Today;

    private DateTime SelectedTo => ToPicker.Date ?? DateTime.Today;

    private CardiMemberResponse? SelectedMember() =>
        MemberPicker.SelectedIndex >= 0 && MemberPicker.SelectedIndex < _members.Count
            ? _members[MemberPicker.SelectedIndex]
            : _members.FirstOrDefault();

    private static string Describe(long bytes) =>
        bytes < 1_048_576
            ? $"{Math.Max(1, bytes / 1024)} KB"
            : $"{bytes / 1_048_576.0:0.#} MB";

    private void ShowOnly(View panel)
    {
        foreach (var candidate in new View[]
                 { SkeletonPanel, ErrorPanel, FormPanel, GeneratingPanel, CompletePanel, FailedPanel })
        {
            candidate.IsVisible = ReferenceEquals(candidate, panel);
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        _page?.Cancel();
        _generation?.Cancel();
        await Shell.Current.GoToAsync("..");
    }
}
