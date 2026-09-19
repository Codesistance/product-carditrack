namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// The app's own palette (Colors.xaml), so a printout and the screen agree.
/// </summary>
/// <remarks>
/// Held in one place now that two documents are rendered from it — the health-data export and the
/// chat transcript. A caregiver may well print both and file them together, and two documents
/// from one product that disagree about what "quiet grey" is look like two products.
/// </remarks>
internal static class ReportPalette
{
    internal const string Ink = "#1F1F1F";
    internal const string Body = "#343434";
    internal const string Secondary = "#727272";
    internal const string Muted = "#939DAA";
    internal const string Divider = "#E2E8F0";
    internal const string Brand = "#1884DC";
    internal const string BrandDark = "#174E86";
    internal const string Tint = "#F4F8FB";
    internal const string TableHead = "#F2F5F9";
    internal const string Zebra = "#FAFBFD";
    internal const string White = "#FFFFFF";
}
