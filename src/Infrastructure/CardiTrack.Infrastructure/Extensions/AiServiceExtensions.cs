using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.ExternalClients.General;
using CardiTrack.Infrastructure.ExternalClients.Medical;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.Infrastructure.Extensions;

/// <summary>
/// Wires the two AI systems this platform runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public</b> (AI:Public) — reports and chat. The provider is chosen by
/// <see cref="PublicAiSettings.Kind"/>, so swapping to another public model is a configuration
/// change. Adding a provider means three edits here — a new <see cref="IExternalAiClient"/>, a
/// <see cref="PublicAiProviderKind"/> member, and its entry in <see cref="DefaultBaseUrls"/> —
/// and nothing downstream of the keyed registration moves.
/// </para>
/// <para>
/// <b>Private</b> (AI:Private) — health insights. Pinned to <see cref="MedGemmaClient"/> in code.
/// There is deliberately no kind switch on this side: the medical prompts carry age, sex and
/// free-text MedicalNotes, and keeping inference in-project is the control the DPIA relies on. A
/// misconfigured environment variable must not be able to send them somewhere else.
/// </para>
/// </remarks>
public static class AiServiceExtensions
{
    /// <summary>Named HTTP client for public providers that use the shared HttpClient plumbing.</summary>
    public const string PublicHttpClientName = "PublicAiClient";

    /// <summary>Named HTTP client for the in-VPC MedGemma service.</summary>
    public const string PrivateHttpClientName = "PrivateAiClient";

    /// <summary>Endpoint used when AI:Public:BaseUrl is not set. Every kind has one.</summary>
    private static readonly Dictionary<PublicAiProviderKind, string> DefaultBaseUrls = new()
    {
        [PublicAiProviderKind.Gemini] = "https://generativelanguage.googleapis.com",
        [PublicAiProviderKind.Anthropic] = "https://api.anthropic.com"
    };

    public static IServiceCollection AddAiServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var publicSettings = LoadPublicSettings(configuration);
        var privateSettings = LoadPrivateSettings(configuration);

        // The Anthropic SDK brings its own transport, so only the HttpClient-based kinds get a
        // named client. Registering one unconditionally would leave a configured-but-unused client
        // behind whenever the public provider is swapped.
        if (RequiresHttpClient(publicSettings.Kind))
        {
            services.AddHttpClient(PublicHttpClientName, client =>
            {
                client.BaseAddress = new Uri(ResolveBaseUrl(publicSettings));
                client.Timeout = TimeSpan.FromSeconds(publicSettings.TimeoutSeconds);
            });
        }

        if (publicSettings.Kind == PublicAiProviderKind.Anthropic)
        {
            // Singleton: the SDK client owns a connection pool, and building one per scope would
            // churn sockets on every request.
            services.AddSingleton(_ => new Anthropic.AnthropicClient
            {
                ApiKey = publicSettings.ApiKey,
                BaseUrl = ResolveBaseUrl(publicSettings),
                Timeout = TimeSpan.FromSeconds(publicSettings.TimeoutSeconds)
            });
        }

        services.AddHttpClient(PrivateHttpClientName, client =>
        {
            client.BaseAddress = new Uri(privateSettings.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(privateSettings.TimeoutSeconds);
        });

        services.AddKeyedScoped<IExternalAiClient>("GeneralProvider", (sp, _) =>
            publicSettings.Kind switch
            {
                PublicAiProviderKind.Gemini => new GeminiClient(
                    sp.GetRequiredService<IHttpClientFactory>(), publicSettings, PublicHttpClientName),
                PublicAiProviderKind.Anthropic => new AnthropicAiClient(
                    sp.GetRequiredService<Anthropic.AnthropicClient>(), publicSettings),
                _ => throw new InvalidOperationException(
                    $"No client is implemented for public AI provider kind '{publicSettings.Kind}'.")
            });

        // Not a switch, by design — see the remarks on this class.
        services.AddKeyedScoped<IExternalAiClient>("MedicalProvider", (sp, _) =>
            new MedGemmaClient(
                sp.GetRequiredService<IHttpClientFactory>(), privateSettings, PrivateHttpClientName));

        services.AddScoped<IGenerativeAiService, GenerativeAiService>();
        services.AddScoped<IMedicalAiService, MedicalAiService>();
        services.AddScoped<IHealthInsightService, HealthInsightService>();
        services.AddScoped<IReportGenerationService, ReportGenerationService>();

        return services;
    }

    /// <summary>
    /// Binds and validates AI:Public. Every failure here is a deployment mistake, so it stops the
    /// host at startup rather than surfacing as a 500 on the first caregiver who opens chat.
    /// </summary>
    private static PublicAiSettings LoadPublicSettings(IConfiguration configuration)
    {
        var section = configuration.GetSection(ConfigurationKeys.AI.PublicSectionName);

        // Parsed before binding: the binder throws on an unknown enum value with a message that
        // names the CLR type rather than the setting the operator has to fix.
        var rawKind = section[nameof(PublicAiSettings.Kind)];
        if (!Enum.TryParse<PublicAiProviderKind>(rawKind, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
        {
            throw new InvalidOperationException(
                Message(ConfigurationKeys.AI.PublicSectionName, nameof(PublicAiSettings.Kind),
                    $"is '{rawKind ?? "(not set)"}'. Supported kinds: {string.Join(", ", Enum.GetNames<PublicAiProviderKind>())}."));
        }

        var settings = section.Get<PublicAiSettings>() ?? new PublicAiSettings();
        settings.Kind = kind;

        RequireValue(settings.Model, ConfigurationKeys.AI.PublicSectionName, nameof(PublicAiSettings.Model));
        RequireValue(settings.ApiKey, ConfigurationKeys.AI.PublicSectionName, nameof(PublicAiSettings.ApiKey));
        RequirePositive(settings.TimeoutSeconds, ConfigurationKeys.AI.PublicSectionName, nameof(PublicAiSettings.TimeoutSeconds));
        RequirePositive(settings.MaxOutputTokens, ConfigurationKeys.AI.PublicSectionName, nameof(PublicAiSettings.MaxOutputTokens));
        RequireAbsoluteUrl(ResolveBaseUrl(settings), ConfigurationKeys.AI.PublicSectionName, nameof(PublicAiSettings.BaseUrl));

        return settings;
    }

    private static PrivateAiSettings LoadPrivateSettings(IConfiguration configuration)
    {
        var settings = configuration.GetSection(ConfigurationKeys.AI.PrivateSectionName).Get<PrivateAiSettings>()
            ?? new PrivateAiSettings();

        RequireValue(settings.Model, ConfigurationKeys.AI.PrivateSectionName, nameof(PrivateAiSettings.Model));
        RequireValue(settings.BaseUrl, ConfigurationKeys.AI.PrivateSectionName, nameof(PrivateAiSettings.BaseUrl));
        RequirePositive(settings.TimeoutSeconds, ConfigurationKeys.AI.PrivateSectionName, nameof(PrivateAiSettings.TimeoutSeconds));
        RequireAbsoluteUrl(settings.BaseUrl, ConfigurationKeys.AI.PrivateSectionName, nameof(PrivateAiSettings.BaseUrl));

        return settings;
    }

    /// <summary>
    /// Whether the kind uses the shared named <see cref="HttpClient"/>. Expressed as an opt-out so a
    /// kind added later gets a configured client by default: an unnecessary registration is inert,
    /// whereas a missing one hands the client a factory default with no base address and fails at
    /// request time instead of at startup.
    /// </summary>
    private static bool RequiresHttpClient(PublicAiProviderKind kind) => kind != PublicAiProviderKind.Anthropic;

    /// <summary>
    /// Falls back to the kind's documented endpoint. A kind with no entry is a gap in this class,
    /// not operator error, so it says so — an unguarded lookup would surface as a bare
    /// KeyNotFoundException naming neither the kind nor the fix.
    /// </summary>
    private static string ResolveBaseUrl(PublicAiSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
            return settings.BaseUrl;

        if (!DefaultBaseUrls.TryGetValue(settings.Kind, out var defaultBaseUrl))
        {
            throw new InvalidOperationException(
                $"No default endpoint is registered for public AI provider kind '{settings.Kind}'. " +
                $"Add one to {nameof(AiServiceExtensions)}.{nameof(DefaultBaseUrls)}, or set " +
                $"'{ConfigurationLoader.ToEnvVarKey($"{ConfigurationKeys.AI.PublicSectionName}:{nameof(PublicAiSettings.BaseUrl)}")}'.");
        }

        return defaultBaseUrl;
    }

    private static void RequireValue(string? value, string section, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(Message(section, key, "is not set."));
    }

    private static void RequirePositive(int value, string section, string key)
    {
        if (value <= 0)
            throw new InvalidOperationException(Message(section, key, $"must be greater than zero (found {value})."));
    }

    /// <summary>
    /// Checks the scheme, not just parseability: "localhost:11434" parses as an absolute URI whose
    /// scheme is "localhost", so a plain absolute-URI check accepts a host:port pasted without a
    /// scheme and the failure resurfaces much later as an unreachable endpoint.
    /// </summary>
    private static void RequireAbsoluteUrl(string value, string section, string key)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                Message(section, key, $"is not an absolute http(s) URL (found '{value}')."));
        }
    }

    /// <summary>Names both the configuration path and the environment variable, since deployment sets the latter.</summary>
    private static string Message(string section, string key, string problem)
    {
        var path = $"{section}:{key}";
        return $"Configuration '{path}' {problem} " +
               $"Set it in appsettings.json or as environment variable '{ConfigurationLoader.ToEnvVarKey(path)}'.";
    }
}
