using Android.App;
using Android.Content;
using Android.Content.PM;

namespace CardiTrack.Mobile.Platforms.Android;

/// <summary>
/// Receives the carditrack:// deep link when a device provider's OAuth page redirects back,
/// handing control to WebAuthenticator (see FitbitConnectionPage.CallbackUri).
/// </summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "carditrack",
    DataHost = "oauth")]
public class WebAuthenticationCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
}
