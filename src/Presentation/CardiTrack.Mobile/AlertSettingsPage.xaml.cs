using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Alerts;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// Which alerts CardiTrack checks for on this CardiMember: the rule catalogue as switches.
/// </summary>
/// <remarks>
/// <para>
/// Every switch saves the moment it is flipped, the way <see cref="JournalTimingPage"/>'s rows
/// do. A failed save puts the switch back and says so; while one save is in flight the other
/// switches refuse to move, so two quick flips cannot race each other to the server.
/// </para>
/// <para>
/// Only the primary caregiver can change a rule. Everyone else sees the same page with the
/// switches disabled, and the intro line says why, so the page never reads as broken.
/// </para>
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(MemberName), "name")]
[QueryProperty(nameof(CanManage), "canManage")]
public partial class AlertSettingsPage : ContentPage
{
    public const string Route = "alertsettings";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _memberId;
    private bool _canManage;
    private AlertPreferencesResponse? _prefs;

    /// <summary>Guards Switch.Toggled while we build or roll back rows.</summary>
    private bool _applying;

    /// <summary>Rule id currently waiting on a PATCH — blocks overlapping toggles.</summary>
    private string? _toggleInFlight;

    public AlertSettingsPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    public string MemberName
    {
        set
        {
            var name = Uri.UnescapeDataString(value ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(name))
                HeaderSubtitle.Text = $"{name}'s alerts";
        }
    }

    /// <summary>
    /// Whether the caregiver may flip a switch. Member Details already knows, so it rides along
    /// on the route rather than costing this page a second member fetch.
    /// </summary>
    public string CanManage
    {
        set => _canManage = bool.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var can) && can;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // A popup closing raises this again; reloading under a caregiver mid-flip would
        // replace the switches they are reading with the same ones fetched twice.
        if (_popups.IsShowing || _prefs is not null)
            return;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _prefs = await _api.GetAlertPreferencesAsync(_memberId);
            Render(_prefs);
        }
        catch (ApiException ex)
        {
            ErrorDetailLabel.Text = ex.Message;
            LoadingSpinner.IsVisible = false;
            LoadingSpinner.IsRunning = false;
            ErrorPanel.IsVisible = true;
        }
    }

    private void Render(AlertPreferencesResponse prefs)
    {
        LoadingSpinner.IsVisible = false;
        LoadingSpinner.IsRunning = false;
        ErrorPanel.IsVisible = false;
        SettingsPanel.IsVisible = true;

        IntroLabel.Text = _canManage
            ? "Turn a rule off and CardiTrack will not check for it."
            : "Turn a rule off and CardiTrack will not check for it. Only the primary caregiver can change these.";

        _applying = true;
        try
        {
            ClustersHost.Clear();
            var resources = Microsoft.Maui.Controls.Application.Current!.Resources;

            // Ordered by availability, at both levels: rules that can actually be turned on or off
            // come before the ones still marked "Soon", and a cluster with nothing available in it
            // yet sinks below the clusters that have something. What the catalogue offers today is
            // what a caregiver came here to change; the reserved ids are a roadmap, and reading
            // past two of them to reach a switch made the list feel mostly unbuilt.
            //
            // Within a cluster the tie is broken by title rather than by the catalogue's order.
            // That order was editorial — the sequence the rules were written in — which is a
            // reasonable default for a list somebody reads through once and a poor one for a list
            // of switches somebody returns to for a specific rule. Alphabetical is the order a
            // reader can predict without knowing the catalogue.
            var clusters = prefs.Clusters
                .OrderBy(c => c.Rules.Any(r => r.IsImplemented) ? 0 : 1);

            foreach (var cluster in clusters)
            {
                var rows = new VerticalStackLayout { Spacing = 0 };
                var first = true;
                foreach (var rule in AlertRuleOrder.ForDisplay(cluster.Rules))
                {
                    if (!first)
                        rows.Add(new BoxView { Style = (Style)resources["DividerLine"] });
                    first = false;
                    rows.Add(BuildRow(rule, resources));
                }

                // The Journal Settings section: a section title, one line on what the group is
                // for, then an outlined card of rows — outlined, not elevated, because it sits on
                // the section card rather than the page ground.
                ClustersHost.Add(new Border
                {
                    Style = (Style)resources["ElevatedCard"],
                    Content = new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children =
                        {
                            new Label
                            {
                                Text = cluster.Title,
                                Style = (Style)resources["DashboardSectionTitle"],
                            },
                            new Label
                            {
                                Text = cluster.Description,
                                Style = (Style)resources["Body2"],
                            },
                            new Border
                            {
                                Style = (Style)resources["OutlinedCard"],
                                Padding = new Thickness(14, 4),
                                Content = rows,
                            },
                        },
                    },
                });
            }
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// One rule as a Journal Settings row: title, one line of what it looks for, and on the right
    /// its switch — or "Soon" for a catalogue id whose rule is not built yet.
    /// </summary>
    private View BuildRow(AlertRuleSettingResponse rule, ResourceDictionary resources)
    {
        var title = new Label
        {
            Text = rule.Title,
            Style = (Style)resources["Body1SemiBoldDark"],
            FontSize = 15,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        var subtitle = new Label
        {
            // The description alone: the "Soon" seat on the right says a rule is not built yet,
            // the way the accordion this replaces had to say it in the subtitle.
            Text = rule.Description,
            Style = (Style)resources["Body2"],
            FontSize = 13,
            LineBreakMode = LineBreakMode.WordWrap,
        };

        var textStack = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { title, subtitle },
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Auto),
            },
            ColumnSpacing = 12,
            Padding = new Thickness(0, 8),
            MinimumHeightRequest = 48,
        };
        SemanticProperties.SetDescription(grid, rule.Title);
        grid.Add(textStack, 0);

        if (!rule.IsImplemented)
        {
            grid.Add(new Label
            {
                Text = "Soon",
                Style = (Style)resources["Body1SemiBoldDark"],
                FontSize = 15,
                TextColor = (Color)resources["Primary"],
                VerticalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center,
            }, 1);
            return grid;
        }

        var toggle = new Switch
        {
            IsToggled = rule.Enabled,
            IsEnabled = _canManage,
            OnColor = (Color)resources["Primary"],
            VerticalOptions = LayoutOptions.Center,
        };
        toggle.Toggled += async (_, args) =>
        {
            if (_applying)
                return;

            if (_toggleInFlight is not null)
            {
                // Another PATCH is in flight — put the switch back and wait.
                _applying = true;
                toggle.IsToggled = !args.Value;
                _applying = false;
                return;
            }

            var previous = !args.Value;
            _toggleInFlight = rule.Id;
            toggle.IsEnabled = false;
            try
            {
                await _api.SetAlertRuleEnabledAsync(_memberId, rule.Id, args.Value);
            }
            catch (ApiException ex) when (!ex.IsSessionExpired)
            {
                _applying = true;
                toggle.IsToggled = previous;
                _applying = false;
                await _popups.ShowErrorAsync(ex.Message, "Couldn't update alert rule");
            }
            catch (ApiException)
            {
                // Session gone — the app is already on its way back to sign-in.
            }
            finally
            {
                _toggleInFlight = null;
                toggle.IsEnabled = _canManage;
            }
        };
        grid.Add(toggle, 1);

        return grid;
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(
            $"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_memberId}");
}
