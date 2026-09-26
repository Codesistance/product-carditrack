namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// The app's own palette (Colors.xaml), so a printout and the screen agree.
/// </summary>
/// <remarks>
/// <para>
/// Held in one place now that two documents are rendered from it — the health-data export and the
/// chat transcript. A caregiver may well print both and file them together, and two documents
/// from one product that disagree about what "quiet grey" is look like two products.
/// </para>
/// <para>
/// <see cref="Caption"/> is the one colour here the app does not have. The screen's quiet grey
/// (#939DAA) is 2.75:1 on white, and on paper it carried the smallest text in the document — the
/// footer, the key-figure ranges, the chart meta — at 7.5pt, where it is the first thing a
/// photocopier or an older reader loses. Caption is the same cool grey taken dark enough to clear
/// 4.5:1 on white and on every tint below.
/// </para>
/// </remarks>
internal static class ReportPalette
{
    internal const string Ink = "#1F1F1F";
    internal const string Body = "#343434";
    internal const string Secondary = "#727272";

    /// <summary>Small and quiet text: 5.8:1 on white, 5.5:1 on <see cref="Tint"/>.</summary>
    internal const string Caption = "#5C6672";

    internal const string Divider = "#E2E8F0";
    internal const string Brand = "#1884DC";
    internal const string BrandDark = "#174E86";
    internal const string Tint = "#F4F8FB";
    internal const string TableHead = "#F2F5F9";
    internal const string Zebra = "#FAFBFD";
    internal const string White = "#FFFFFF";
}
