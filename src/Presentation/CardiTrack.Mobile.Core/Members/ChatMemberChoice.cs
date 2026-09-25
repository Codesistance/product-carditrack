namespace CardiTrack.Mobile.Core.Members;

/// <summary>
/// The one line under a member's name when chat asks who a conversation is about — and the
/// colour of the dot in front of it, as a Colors.xaml key.
/// </summary>
/// <remarks>
/// <para>
/// Said the way the dashboard says it, so picking someone in chat never contradicts the card the
/// caregiver just left: the hero's own headline for the tier when there is one, or the line the
/// dashboard last saved for that member when it is still about the same tier.
/// </para>
/// <para>
/// An unknown tier with readings behind it gets no line at all. The dashboard reads that state
/// back as the day's numbers, which do not fit on a row, and anything shorter ("still learning")
/// would report on CardiTrack rather than on the member.
/// </para>
/// </remarks>
public static class ChatMemberChoice
{
    public const string NoReadingsYet = "No readings yet";

    /// <param name="healthStatus">The dashboard's tier: red, orange, yellow, green, paused or unknown.</param>
    /// <param name="paused">Monitoring is paused for this member.</param>
    /// <param name="everSynced">Any reading has ever reached us for this member.</param>
    /// <param name="savedHeadline">The dashboard's last saved status headline for this tier, if any.</param>
    public static (string? Text, string ColorKey) Status(
        string? healthStatus, bool paused, bool everSynced, string? savedHeadline)
    {
        if (paused || healthStatus == "paused")
            return ("Monitoring paused", "StatusUnknown");
        if (!everSynced)
            return (NoReadingsYet, "StatusUnknown");

        var colorKey = healthStatus switch
        {
            "red" => "StatusRed",
            "orange" => "StatusOrange",
            "yellow" => "StatusYellow",
            "green" => "StatusGreen",
            _ => "StatusUnknown",
        };

        if (!string.IsNullOrWhiteSpace(savedHeadline))
            return (savedHeadline.Trim(), colorKey);

        return healthStatus switch
        {
            "green" => ("All steady", colorKey),
            "yellow" => ("Something's different", colorKey),
            "orange" => ("Worth a check-in", colorKey),
            "red" => ("Reach out now", colorKey),
            _ => (null, colorKey),
        };
    }
}
