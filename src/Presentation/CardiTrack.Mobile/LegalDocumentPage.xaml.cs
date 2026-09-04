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
/// Fetched with <c>?embed</c>, which the site answers with the same document minus its own
/// header, hero and footer, set in the app's face and inks. Before that switch existed the
/// reader showed the marketing page whole: a second sticky header under ours, a navy banner,
/// the site footer, and justified prose hyphenated mid-word at phone width.
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

    /// <summary>
    /// The site strips its own chrome for this query. Kept on every navigation the reader makes
    /// — a cross-reference from the terms to the privacy policy has to arrive stripped too, or
    /// the caregiver lands back on the marketing page one tap in.
    /// </summary>
    private const string EmbedQuery = "?embed";

    private const string TermsPath = SiteRoot + "/terms-of-service";
    private const string PrivacyPath = SiteRoot + "/privacy-policy";

    internal const string TermsUrl = TermsPath + EmbedQuery;
    internal const string PrivacyUrl = PrivacyPath + EmbedQuery;

    internal const string TermsTitle = "Terms of Service";
    internal const string PrivacyTitle = "Privacy Policy";

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
    /// Everything the document links to is decided here. The two legal documents cross-reference
    /// each other, so those stay in the reader — with the embed query re-attached, since the link
    /// in the copy points at the plain URL. Everything else — the site's own nav, a mailto: to
    /// support — belongs to the phone: a caregiver who taps "contact us" should not end up
    /// browsing carditrack.com inside a modal with one way out.
    /// </summary>
    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (IsLegalDocument(e.Url))
        {
            if (e.Url.Contains("embed", StringComparison.OrdinalIgnoreCase))
                return;

            e.Cancel = true;
            DocumentView.Source = new UrlWebViewSource { Url = e.Url + EmbedQuery };
            return;
        }

        e.Cancel = true;
        OpenExternally(e.Url);
    }

    private static bool IsLegalDocument(string url) => TitleFor(url) is not null;

    /// <summary>
    /// Which document a URL is, or null for anything that is not one of the two. Also what keeps
    /// the header honest: follow the terms' link to the privacy policy and the band above it has
    /// to stop saying "Terms of Service".
    /// </summary>
    private static string? TitleFor(string url) =>
        url.StartsWith(TermsPath, StringComparison.OrdinalIgnoreCase) ? TermsTitle
        : url.StartsWith(PrivacyPath, StringComparison.OrdinalIgnoreCase) ? PrivacyTitle
        : null;

    /// <summary>
    /// Fire-and-forget by design: the navigation is already cancelled, and a phone with no app
    /// willing to open the link leaves the document exactly where it was, which is the right
    /// outcome — there is nothing useful to say about a link that went nowhere.
    /// </summary>
    private static async void OpenExternally(string url)
    {
        try
        {
            await Launcher.Default.OpenAsync(url);
        }
        catch (Exception)
        {
            // No handler for the scheme, or the launcher refused it.
        }
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
            if (TitleFor(e.Url) is { } title)
                TitleLabel.Text = title;

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
