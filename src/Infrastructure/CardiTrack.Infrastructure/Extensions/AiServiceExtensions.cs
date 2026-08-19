using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.ExternalClients.General;
using CardiTrack.Infrastructure.ExternalClients.Medical;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Services.PromptContext;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// <para>
/// <b>Rewrite</b> (AI:Rewrite) — a third, equally in-estate slot. Same <see cref="MedGemmaClient"/>
/// pointed at a second, plain non-medical model tag pulled into the same MedGemma image
/// (docs/llm_design.md), used to rewrite MedGemma's clinical output into caregiver language
/// without any clinical content ever leaving the project. Registered inside
/// <see cref="AddMedicalAiServices"/> for the same reason Private is not switchable: it must
/// travel with the private slot on every host, never be reachable on its own.
/// </para>
/// </remarks>
public static class AiServiceExtensions
{
    /// <summary>Named HTTP client for public providers that use the shared HttpClient plumbing.</summary>
    public const string PublicHttpClientName = "PublicAiClient";

    /// <summary>Named HTTP client for the in-VPC MedGemma service.</summary>
    public const string PrivateHttpClientName = "PrivateAiClient";

    /// <summary>Named HTTP client for the in-VPC rewrite model — same host as Private, own client
    /// instance so its timeout/base-address can be configured independently.</summary>
    public const string RewriteHttpClientName = "RewriteAiClient";

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

        services.AddMedicalAiServices(configuration);

        services.AddScoped<IGenerativeAiService, GenerativeAiService>();
        services.AddScoped<IHealthInsightService, HealthInsightService>();
        services.AddScoped<IReportGenerationService, ReportGenerationService>();

        return services;
    }

    /// <summary>
    /// The private (medical) slot alone — for hosts like the digest pipeline job that call only
    /// MedGemma. Deliberately narrower than <see cref="AddAiServices"/>: a host wired through this
    /// method carries no public-provider key and no public client, so it physically cannot send
    /// health data off-estate — the same A5 boundary the DPIA records, enforced by what is not
    /// registered.
    /// </summary>
    public static IServiceCollection AddMedicalAiServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var privateSettings = LoadPrivateSettings(configuration);

        var privateClient = services.AddHttpClient(PrivateHttpClientName, client =>
        {
            client.BaseAddress = new Uri(privateSettings.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(privateSettings.TimeoutSeconds);
        });

        // Added only when the flag is on, rather than registering a handler that decides per request:
        // a local Ollama then has no auth code in its pipeline at all, and the deployed shape has no
        // branch that could silently skip the credential.
        if (privateSettings.UseIdentityToken)
        {
            privateClient.AddHttpMessageHandler(sp => new MedGemmaIdentityTokenHandler(
                privateSettings.BaseUrl,
                sp.GetRequiredService<ILogger<MedGemmaIdentityTokenHandler>>()));
        }

        // Not a switch, by design — see the remarks on this class.
        services.AddKeyedScoped<IExternalAiClient>("MedicalProvider", (sp, _) =>
            new MedGemmaClient(
                sp.GetRequiredService<IHttpClientFactory>(), privateSettings, PrivateHttpClientName,
                sp.GetRequiredService<ILogger<MedGemmaClient>>()));

        services.AddScoped<IMedicalAiService, MedicalAiService>();

        // Also resolvable on its own: HealthInsightService needs the request-path budget, and this
        // is the same instance the client above was handed rather than a second read of config.
        services.AddSingleton(privateSettings);

        // Rewrite slot — a second, plain non-medical model pulled into the same MedGemma image
        // (docs/llm_design.md), used only to rewrite MedGemma's clinical output into caregiver
        // language. Registered here, not AddAiServices, so it travels with the private slot on
        // every host that gets it and is unreachable from any host that doesn't — no host can end
        // up with the rewrite model wired but not MedGemma. Same in-estate guarantee as Private:
        // MedGemmaClient again, just a different settings instance and a different client name.
        var rewriteSettings = LoadRewriteSettings(configuration);

        var rewriteClient = services.AddHttpClient(RewriteHttpClientName, client =>
        {
            client.BaseAddress = new Uri(rewriteSettings.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(rewriteSettings.TimeoutSeconds);
        });

        if (rewriteSettings.UseIdentityToken)
        {
            rewriteClient.AddHttpMessageHandler(sp => new MedGemmaIdentityTokenHandler(
                rewriteSettings.BaseUrl,
                sp.GetRequiredService<ILogger<MedGemmaIdentityTokenHandler>>()));
        }

        services.AddKeyedScoped<IExternalAiClient>("RewriteProvider", (sp, _) =>
            new MedGemmaClient(
                sp.GetRequiredService<IHttpClientFactory>(), rewriteSettings, RewriteHttpClientName,
                sp.GetRequiredService<ILogger<MedGemmaClient>>()));

        services.AddScoped<IRewriteAiService, RewriteAiService>();

        // Member context — the composer and every source that feeds it. Registered here rather
        // than in each host's composition root deliberately: this method is the one door every
        // MedGemma-calling host goes through, so a host cannot end up with the composer and a
        // different set of sources than its neighbour. Adding a context source is one line here
        // and it reaches every prompt the source declares itself relevant to.
        services.AddScoped<MemberContextComposer>();
        services.AddScoped<IMemberContextSource, DemographicsContextSource>();
        services.AddScoped<IMemberContextSource, EnvironmentalContextSource>();
        services.AddScoped<IMemberContextSource, MonitoringContextSource>();
        services.AddScoped<IMemberContextSource, QuestionnaireAnswersContextSource>();

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

        RequireCoherentIdentityTokenMode(ConfigurationKeys.AI.PrivateSectionName, settings.BaseUrl, settings.UseIdentityToken);

        return settings;
    }

    /// <summary>
    /// The plain, non-medical rewrite model — same Cloud Run host as Private in every deployed
    /// environment (same Ollama instance, a different <c>model</c> value per call), validated as
    /// its own section so a host that only wires the rewrite slot is not silently coupled to
    /// Private's config.
    /// </summary>
    private static RewriteAiSettings LoadRewriteSettings(IConfiguration configuration)
    {
        var settings = configuration.GetSection(ConfigurationKeys.AI.RewriteSectionName).Get<RewriteAiSettings>()
            ?? new RewriteAiSettings();

        RequireValue(settings.Model, ConfigurationKeys.AI.RewriteSectionName, nameof(RewriteAiSettings.Model));
        RequireValue(settings.BaseUrl, ConfigurationKeys.AI.RewriteSectionName, nameof(RewriteAiSettings.BaseUrl));
        RequirePositive(settings.TimeoutSeconds, ConfigurationKeys.AI.RewriteSectionName, nameof(RewriteAiSettings.TimeoutSeconds));
        RequireAbsoluteUrl(settings.BaseUrl, ConfigurationKeys.AI.RewriteSectionName, nameof(RewriteAiSettings.BaseUrl));

        RequireCoherentIdentityTokenMode(ConfigurationKeys.AI.RewriteSectionName, settings.BaseUrl, settings.UseIdentityToken);

        return settings;
    }

    /// <summary>
    /// Fails the host at startup when the identity-token mode does not match where MedGemma lives.
    /// </summary>
    /// <remarks>
    /// Both directions matter, and neither is theoretical.
    /// <list type="bullet">
    /// <item>
    /// A Cloud Run <c>BaseUrl</c> with the flag off means every call gets a 403 from the Google front
    /// end. <c>RealtimeAssessmentService</c> and <c>DigestGenerationService</c> swallow per-member
    /// inference failures, so that 403 would not fail a job — it would read as "nothing was due", and
    /// assessments would simply stop being produced. One missed environment variable, silent for as
    /// long as nobody checks. Refusing to boot converts it into an obvious failed revision.
    /// </item>
    /// <item>
    /// The flag on with a plaintext <c>BaseUrl</c> would put a bearer credential on the wire in
    /// clear text.
    /// </item>
    /// </list>
    /// <para>
    /// Takes the settings as primitives rather than <see cref="PrivateAiSettings"/> directly so
    /// <see cref="LoadRewriteSettings"/> — a different settings type pointed at the same kind of
    /// host — can share the check rather than duplicate it.
    /// </para>
    /// </remarks>
    private static void RequireCoherentIdentityTokenMode(string sectionName, string baseUrl, bool useIdentityToken)
    {
        var baseUri = new Uri(baseUrl);
        var isCloudRun = baseUri.Host.EndsWith(".run.app", StringComparison.OrdinalIgnoreCase);
        var useIdentityTokenKey = sectionName == ConfigurationKeys.AI.RewriteSectionName
            ? nameof(RewriteAiSettings.UseIdentityToken)
            : nameof(PrivateAiSettings.UseIdentityToken);
        var baseUrlKey = sectionName == ConfigurationKeys.AI.RewriteSectionName
            ? nameof(RewriteAiSettings.BaseUrl)
            : nameof(PrivateAiSettings.BaseUrl);

        if (isCloudRun && !useIdentityToken)
        {
            throw new InvalidOperationException(
                Message(sectionName, useIdentityTokenKey,
                    $"is false, but {sectionName}:{baseUrlKey} "
                    + $"points at the Cloud Run host '{baseUri.Host}', which authorises callers by IAM. "
                    + "Every MedGemma call would return 403 before reaching the model, and because "
                    + "per-member inference failures are swallowed, digests and assessments would stop "
                    + $"silently rather than erroring. Set {ConfigurationLoader.ToEnvVarKey($"{sectionName}:{useIdentityTokenKey}")}=true."));
        }

        if (useIdentityToken && !baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                Message(sectionName, useIdentityTokenKey,
                    $"is true, but {sectionName}:{baseUrlKey} "
                    + $"uses scheme '{baseUri.Scheme}'. That would transmit a bearer identity token in "
                    + "clear text. Use https, or turn the flag off for a local endpoint."));
        }
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
