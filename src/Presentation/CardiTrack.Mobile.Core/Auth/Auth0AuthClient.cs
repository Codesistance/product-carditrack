using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CardiTrack.Mobile.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Core.Auth;

/// <summary>
/// Auth0 Authentication API client for a public native application (no client secret).
/// Login uses the password-realm grant; signup and password reset use /dbconnections.
/// Log entries carry the operation, HTTP status, and Auth0 error code — never
/// credentials, tokens, or emails.
/// </summary>
public sealed class Auth0AuthClient : IAuth0AuthClient
{
    private const string PasswordRealmGrant = "http://auth0.com/oauth/grant-type/password-realm";
    private const string Scopes = "openid profile email offline_access";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Auth0Options _options;
    private readonly ILogger<Auth0AuthClient> _logger;

    public Auth0AuthClient(HttpClient http, Auth0Options options, ILogger<Auth0AuthClient>? logger = null)
    {
        _http = http;
        _options = options;
        _logger = logger ?? NullLogger<Auth0AuthClient>.Instance;
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
        return await SendTokenRequestAsync("login", form, ct);
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
            throw NetworkError("signup", ex);
        }

        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var signupError = MapSignupError(body);
        _logger.LogWarning("Auth0 signup failed with {StatusCode}: {AuthErrorCode} ({Auth0Error})",
            (int)response.StatusCode, signupError.Code, signupError.Auth0Error);
        throw signupError;
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
            throw NetworkError("password-reset", ex);
        }

        // Auth0 returns 200 with a plain-text body whether or not the account exists
        // (no user enumeration) — any 2xx is success.
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var error = MapTokenError(response, body);
            _logger.LogWarning("Auth0 password reset failed with {StatusCode}: {AuthErrorCode} ({Auth0Error})",
                (int)response.StatusCode, error.Code, error.Auth0Error);
            throw error;
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
        return await SendTokenRequestAsync("token-refresh", form, ct);
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
            _logger.LogWarning(ex, "Auth0 token revocation failed with a transport error; continuing sign-out");
        }
    }

    private async Task<AuthTokens> SendTokenRequestAsync(string operation, Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync("/oauth/token", new FormUrlEncodedContent(form), ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError(operation, ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = MapTokenError(response, body);
            _logger.LogWarning("Auth0 {Operation} failed with {StatusCode}: {AuthErrorCode} ({Auth0Error})",
                operation, (int)response.StatusCode, error.Code, error.Auth0Error);
            throw error;
        }

        var payload = JsonSerializer.Deserialize<TokenResponse>(body, Json);
        if (payload is null || string.IsNullOrEmpty(payload.AccessToken))
        {
            _logger.LogError("Auth0 {Operation} returned {StatusCode} but the token payload was empty or incomplete",
                operation, (int)response.StatusCode);
            throw new AuthException(AuthErrorCode.Unknown, payload is null
                ? "Empty token response from Auth0."
                : "Auth0 response did not include an access token.");
        }

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

    private AuthException NetworkError(string operation, Exception ex)
    {
        _logger.LogError(ex, "Auth0 {Operation} failed with a transport error", operation);
        return new(AuthErrorCode.Network, "No connection. Check your internet and try again.", inner: ex);
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
