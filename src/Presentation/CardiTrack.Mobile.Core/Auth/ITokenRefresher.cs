namespace CardiTrack.Mobile.Core.Auth;

public interface ITokenRefresher
{
    /// <summary>
    /// Returns a currently-valid access token, refreshing it when expired or when
    /// <paramref name="forceRefresh"/> is set. Returns null when signed out or when the
    /// session could not be restored.
    /// </summary>
    Task<string?> GetValidAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>Raised when the refresh token is rejected and the stored session was cleared.</summary>
    event Action? SessionExpired;
}
