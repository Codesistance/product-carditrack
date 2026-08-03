using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CardiTrack.Mobile.Core.Configuration;

namespace CardiTrack.Mobile.Core.Auth;

/// <summary>
/// Auth0 Authentication API client for a public native application (no client secret).
/// Login uses the password-realm grant; signup and password reset use /dbconnections.
/// </summary>
public sealed class Auth0AuthClient : IAuth0AuthClient
{
    private const string PasswordRealmGrant = "http://auth0.com/oauth/grant-type/password-realm";
    private const string Scopes = "openid profile email offline_access";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Auth0Options _options;

    public Auth0AuthClient(HttpClient http, Auth0Options options)
    {
        _http = http;
        _options = options;
    }

    public async Task<AuthTokens> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        EnsureConfigured();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = PasswordRealmGrant,
            ["realm"] = Auth0Options.DbConnection,
            ["client_id"] = _options.ClientId,
            ["audience"] = _options.Audience,
            ["scope"] = Scopes,
            ["username"] = email,
            ["password"] = password,
        };
        return await SendTokenRequestAsync(form, ct);
    }

    public async Task SignUpAsync(string name, string email, string password, CancellationToken ct = default)
    {
        EnsureConfigured();
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("/dbconnections/signup", new
            {
                client_id = _options.ClientId,
                email,
                password,
                connection = Auth0Options.DbConnection,
                name,
            }, Json, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError(ex);
        }

        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw MapSignupError(body);
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken ct = default)
    {
        EnsureConfigured();
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("/dbconnections/change_password", new
            {
                client_id = _options.ClientId,
                email,
                connection = Auth0Options.DbConnection,
            }, Json, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError(ex);
        }

        // Auth0 returns 200 with a plain-text body whether or not the account exists
        // (no user enumeration) — any 2xx is success.
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw MapTokenError(response, body);
        }
    }

    public async Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        EnsureConfigured();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _options.ClientId,
            ["refresh_token"] = refreshToken,
        };
        return await SendTokenRequestAsync(form, ct);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            await _http.PostAsJsonAsync("/oauth/revoke", new
            {
                client_id = _options.ClientId,
                token = refreshToken,
            }, Json, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            // Best-effort: revocation failure must not block sign-out.
        }
    }

    private async Task<AuthTokens> SendTokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync("/oauth/token", new FormUrlEncodedContent(form), ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError(ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw MapTokenError(response, body);

        var payload = JsonSerializer.Deserialize<TokenResponse>(body, Json)
            ?? throw new AuthException(AuthErrorCode.Unknown, "Empty token response from Auth0.");
        if (string.IsNullOrEmpty(payload.AccessToken))
            throw new AuthException(AuthErrorCode.Unknown, "Auth0 response did not include an access token.");

        return new AuthTokens(
            payload.AccessToken,
            payload.RefreshToken,
            payload.IdToken,
            DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn));
    }

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured)
            throw new AuthException(AuthErrorCode.NotConfigured,
                "Auth0 is not configured for this build. Set Auth0Domain/Auth0ClientId/Auth0Audience (Local.props or CI -p: properties).");
    }

    private static AuthException MapTokenError(HttpResponseMessage response, string body)
    {
        string? error = null, description = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString();
            if (doc.RootElement.TryGetProperty("error_description", out var d)) description = d.GetString();
        }
        catch (JsonException) { }

        return error switch
        {
            "invalid_grant" => new AuthException(AuthErrorCode.InvalidCredentials,
                "Wrong email or password.", error, description),
            "too_many_attempts" => new AuthException(AuthErrorCode.TooManyAttempts,
                "Too many attempts. Try again later or reset your password.", error, description),
            _ => new AuthException(AuthErrorCode.Unknown,
                $"Sign-in failed ({(int)response.StatusCode}). Please try again.", error, description),
        };
    }

    private static AuthException MapSignupError(string body)
    {
        string? code = null, name = null, description = null, policy = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var c)) code = c.GetString();
            if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString();
            if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                description = d.GetString();
            if (doc.RootElement.TryGetProperty("policy", out var p)) policy = p.GetString();
        }
        catch (JsonException) { }

        var key = code ?? name;
        return key switch
        {
            "user_exists" or "username_exists" => new AuthException(AuthErrorCode.UserAlreadyExists,
                "An account with this email already exists. Sign in instead.", key, description),
            "invalid_password" or "PasswordStrengthError" => new AuthException(AuthErrorCode.WeakPassword,
                "Password doesn't meet the security requirements.", key, policy ?? description),
            _ => new AuthException(AuthErrorCode.Unknown,
                "We couldn't create your account. Please try again.", key, description),
        };
    }

    private static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException;

    private static AuthException NetworkError(Exception ex) =>
        new(AuthErrorCode.Network, "No connection. Check your internet and try again.", inner: ex);

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
