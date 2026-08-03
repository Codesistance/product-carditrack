namespace CardiTrack.Mobile.Services;

/// <summary>
/// Service locator for pages created with `new` (this app does not resolve pages from DI;
/// navigation swaps window roots via WindowNavigation).
/// </summary>
public static class ServiceHelper
{
    public static T GetRequiredService<T>() where T : notnull
    {
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI services are not available yet.");
        return services.GetRequiredService<T>();
    }
}
