namespace CardiTrack.Mobile;

/// <summary>
/// The terms of service and the privacy policy, shown inside the app.
/// </summary>
/// <remarks>
/// <para>
/// These are linked from the account-creation form, next to the checkbox that agrees to them.
/// Handing them to the phone's browser puts a half-filled form behind an app switch, and asks
/// someone to read a long document in a place that has no way back to what they were doing. A
/// modal keeps the form exactly where they left it.
/// </para>
/// <para>
/// Fetched from the site rather than bundled: these documents change without the app changing,
/// and a copy compiled into a release is a copy that goes stale the first time legal edits a
/// line — while still being the version the app claims someone agreed to.
/// </para>
/// </remarks>
public partial class LegalDocumentPage : ContentPage
{
    /// <summary>
    /// Where the documents live. One constant rather than two literals, because the pair of them
    /// move together and the only thing that ever differs is the path.
    /// </summary>
    private const string SiteRoot = "https://carditrack.com";

    internal const string TermsUrl = SiteRoot + "/terms-of-service";
    internal const string PrivacyUrl = SiteRoot + "/privacy-policy";

    private readonly string _url;

    public LegalDocumentPage(string title, string url)
    {
        InitializeComponent();
        _url = url;
        TitleLabel.Text = title;
        Load();
    }

    private void Load()
    {
        LoadingPanel.IsVisible = true;
        ErrorPanel.IsVisible = false;
        DocumentView.IsVisible = false;
        DocumentView.Source = new UrlWebViewSource { Url = _url };
    }

    /// <summary>
    /// Success and failure both arrive here — <see cref="WebNavigatedEventArgs.Result"/> carries
    /// which. A WebView that cannot reach the network renders its own error page, so without this
    /// the caregiver would be shown Chrome's offline dinosaur inside CardiTrack.
    /// </summary>
    private void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        LoadingPanel.IsVisible = false;

        if (e.Result == WebNavigationResult.Success)
        {
            DocumentView.IsVisible = true;
            return;
        }

        DocumentView.IsVisible = false;
        ErrorDetailLabel.Text = e.Result == WebNavigationResult.Timeout
            ? "The page took too long to load. Check your connection and try again."
            : "We couldn't reach carditrack.com just now.";
        ErrorPanel.IsVisible = true;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => Load();

    private async void OnCloseTapped(object? sender, EventArgs e)
    {
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception)
        {
            // async void from a gesture: a pop that races a dismiss already under way has nothing
            // left to do, and there is no state here worth surfacing an error over.
        }
    }
}
