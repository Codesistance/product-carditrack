using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.ExternalClients.General;
using CardiTrack.Infrastructure.ExternalClients.Medical;
using CardiTrack.Infrastructure.ExternalClients.Vertex;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Extensions;

/// <summary>
/// Startup wiring and validation for the two AI systems.
/// </summary>
/// <remarks>
/// Two things are being pinned here. First, that swapping the public provider is a configuration
/// change — if resolving a kind ever needs a code edit, these tests stop compiling. Second, and
/// more important, that the medical slot is <em>not</em> swappable: it must resolve to MedGemma no
/// matter what the public section says, because those prompts carry health data that the DPIA
/// assumes never leaves the project.
/// </remarks>
public class AiServiceExtensionsTests
{
    [Fact]
    public void AddAiServices_Resolves_GeminiForTheGeminiKind()
    {
        var provider = Resolve(Config());

        var client = provider.GetRequiredKeyedService<IExternalAiClient>("GeneralProvider");

        Assert.IsType<GeminiClient>(client);
    }

    [Fact]
    public void AddAiServices_Resolves_AnthropicForTheAnthropicKind()
    {
        var config = Config();
        config["AI:Public:Kind"] = "Anthropic";
        config["AI:Public:Model"] = "claude-opus-5";

        var client = Resolve(config).GetRequiredKeyedService<IExternalAiClient>("GeneralProvider");

        Assert.IsType<AnthropicAiClient>(client);
    }

    [Theory]
    [InlineData("Gemini")]
    [InlineData("Anthropic")]
    public void AddAiServices_PinsTheMedicalProviderToMedGemma_WhateverThePublicProviderIs(string kind)
    {
        var config = Config();
        config["AI:Public:Kind"] = kind;

        var client = Resolve(config).GetRequiredKeyedService<IExternalAiClient>("MedicalProvider");

        Assert.IsType<MedGemmaClient>(client);
    }

    /// <summary>
    /// The warm-up seam has to land on the same client the next real call will use — a warm-up
    /// that loaded some other model would be indistinguishable from one that worked, right up
    /// until the caregiver waited the full minute anyway.
    /// </summary>
    [Fact]
    public void AddAiServices_ResolvesTheWarmUpClient_ToTheMedicalProvider()
    {
        var provider = Resolve(Config());

        var warmUp = provider.GetRequiredService<IAiWarmUpClient>();

        Assert.Same(provider.GetRequiredKeyedService<IExternalAiClient>("MedicalProvider"), warmUp);
    }

    [Fact]
    public void AddMedicalAiServices_RegistersTheWarmUpService_ForHostsWithOnlyThePrivateSlot()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Config()).Build();
        var provider = new ServiceCollection().AddLogging()
            .AddMedicalAiServices(configuration).BuildServiceProvider();

        Assert.IsType<MedGemmaWarmUpService>(provider.GetRequiredService<IAiWarmUpService>());
    }

    [Fact]
    public void AddAiServices_Throws_WhenWarmUpIsOnWithNoInterval()
    {
        var config = Config();
        config["AI:Private:WarmUpMinimumIntervalSeconds"] = "0";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI:Private:WarmUpMinimumIntervalSeconds", ex.Message);
    }

    /// <summary>
    /// A stale zero left in a config that has warm-up switched off describes nothing, and
    /// refusing to boot over it would be a failure with no reader.
    /// </summary>
    [Fact]
    public void AddAiServices_IgnoresTheInterval_WhenWarmUpIsOff()
    {
        var config = Config();
        config["AI:Private:WarmUpEnabled"] = "false";
        config["AI:Private:WarmUpMinimumIntervalSeconds"] = "0";

        Assert.NotNull(Resolve(config).GetRequiredService<IAiWarmUpService>());
    }

    // With no Kind configured the rewrite slot defaults to Ollama — the local-dev shape must
    // keep working with zero configuration and no GCP credentials.
    [Fact]
    public void AddAiServices_ResolvesTheRewriteProviderToMedGemma_WhenNoKindIsSet()
    {
        var client = Resolve(Config()).GetRequiredKeyedService<IExternalAiClient>("RewriteProvider");

        Assert.IsType<MedGemmaClient>(client);
    }

    [Fact]
    public void AddAiServices_ResolvesTheRewriteProviderToVertex_ForTheVertexGeminiKind()
    {
        var client = Resolve(VertexRewriteConfig()).GetRequiredKeyedService<IExternalAiClient>("RewriteProvider");

        Assert.IsType<VertexAiClient>(client);
    }

    [Fact]
    public void AddAiServices_DerivesTheRewriteBaseAddressFromTheLocation_ForTheVertexGeminiKind()
    {
        var factory = Resolve(VertexRewriteConfig()).GetRequiredService<IHttpClientFactory>();

        Assert.Equal(
            new Uri("https://europe-west2-aiplatform.googleapis.com"),
            factory.CreateClient(AiServiceExtensions.RewriteHttpClientName).BaseAddress);
    }

    [Theory]
    [InlineData("AI:Rewrite:ProjectId", "AI__Rewrite__ProjectId")]
    [InlineData("AI:Rewrite:Location", "AI__Rewrite__Location")]
    public void AddAiServices_Throws_WhenAVertexRewriteAddressPartIsMissing(string key, string expectedEnvVar)
    {
        var config = VertexRewriteConfig();
        config.Remove(key);

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains(expectedEnvVar, ex.Message);
    }

    // During the provider transition the deployed environment still mounts the old Ollama URL and
    // identity-token flag alongside the Vertex settings; rejecting them would make the flip an
    // ordering hazard, so they are tolerated and ignored.
    [Fact]
    public void AddAiServices_ToleratesStaleOllamaSettings_ForTheVertexGeminiKind()
    {
        var config = VertexRewriteConfig();
        config["AI:Rewrite:BaseUrl"] = "https://carditrack-dev-rewrite-abcdef.a.run.app";
        config["AI:Rewrite:UseIdentityToken"] = "true";

        var client = Resolve(config).GetRequiredKeyedService<IExternalAiClient>("RewriteProvider");

        Assert.IsType<VertexAiClient>(client);
    }

    // The DPIA's EU-processing rule, enforced at the second layer: Terraform validates the
    // tfvar, and this refuses the raw env var that bypasses it — "global" and a US region are
    // exactly the values that would route prompts outside the boundary with no other symptom.
    [Theory]
    [InlineData("us-central1")]
    [InlineData("global")]
    [InlineData("Europe-West2")] // a case variant is a wrong request path, not a different region
    public void AddAiServices_Throws_WhenARewriteVertexLocationIsOutsideTheEuAllowlist(string location)
    {
        var config = VertexRewriteConfig();
        config["AI:Rewrite:Location"] = location;

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Rewrite__Location", ex.Message);
        Assert.Contains("europe-west2", ex.Message);
    }

    [Fact]
    public void AddAiServices_Throws_WhenAPublicVertexLocationIsOutsideTheEuAllowlist()
    {
        var config = Config();
        config["AI:Public:Kind"] = "VertexGemini";
        config["AI:Public:ProjectId"] = "test-project";
        config["AI:Public:Location"] = "us-central1";
        config["AI:Public:BaseUrl"] = string.Empty;

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Public__Location", ex.Message);
    }

    // A Vertex request carries a live ADC bearer token, so an endpoint override may only name an
    // allowed-region Vertex host or a loopback test double — anything else is a one-variable
    // credential-exfiltration and EU-bypass vector.
    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("https://us-central1-aiplatform.googleapis.com")]
    [InlineData("http://europe-west2-aiplatform.googleapis.com")] // bearer token over plaintext
    public void AddAiServices_Throws_WhenARewriteVertexEndpointOverrideIsNotVertexOrLoopback(string url)
    {
        var config = VertexRewriteConfig();
        config["AI:Rewrite:VertexBaseUrl"] = url;

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Rewrite__VertexBaseUrl", ex.Message);
    }

    [Fact]
    public void AddAiServices_AcceptsALoopbackVertexEndpointOverride_ForTestDoubles()
    {
        var config = VertexRewriteConfig();
        config["AI:Rewrite:VertexBaseUrl"] = "http://localhost:8080";

        var factory = Resolve(config).GetRequiredService<IHttpClientFactory>();

        Assert.Equal(
            new Uri("http://localhost:8080"),
            factory.CreateClient(AiServiceExtensions.RewriteHttpClientName).BaseAddress);
    }

    [Fact]
    public void AddAiServices_Throws_WhenAPublicVertexEndpointOverrideIsNotVertexOrLoopback()
    {
        var config = Config();
        config["AI:Public:Kind"] = "VertexGemini";
        config["AI:Public:ProjectId"] = "test-project";
        config["AI:Public:Location"] = "europe-west2";
        config["AI:Public:BaseUrl"] = "https://evil.example.com";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Public__BaseUrl", ex.Message);
    }

    // An env-var override that is set-but-empty must read as "not set" — Uri("") at registration
    // time would otherwise turn a blank variable into a failed revision with an opaque error.
    [Fact]
    public void AddAiServices_DerivesTheRewriteBaseAddress_WhenTheVertexBaseUrlOverrideIsBlank()
    {
        var config = VertexRewriteConfig();
        config["AI:Rewrite:VertexBaseUrl"] = string.Empty;

        var factory = Resolve(config).GetRequiredService<IHttpClientFactory>();

        Assert.Equal(
            new Uri("https://europe-west2-aiplatform.googleapis.com"),
            factory.CreateClient(AiServiceExtensions.RewriteHttpClientName).BaseAddress);
    }

    [Fact]
    public void AddAiServices_Throws_WhenTheRewriteKindIsUnknown()
    {
        var config = Config();
        config["AI:Rewrite:Kind"] = "Llama";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Rewrite__Kind", ex.Message);
        Assert.Contains("VertexGemini", ex.Message);
    }

    [Fact]
    public void AddAiServices_AppliesThePerKindBaseUrl_WhenNoneIsConfigured()
    {
        var config = Config();
        config["AI:Public:BaseUrl"] = string.Empty;

        var factory = Resolve(config).GetRequiredService<IHttpClientFactory>();

        Assert.Equal(
            new Uri("https://generativelanguage.googleapis.com"),
            factory.CreateClient(AiServiceExtensions.PublicHttpClientName).BaseAddress);
    }

    [Fact]
    public void AddAiServices_Throws_WhenThePublicKindIsUnknown()
    {
        var config = Config();
        config["AI:Public:Kind"] = "Llama";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        // The operator sets this through deployment, so the message has to name the variable
        // they actually edit, not the CLR type the binder failed on.
        Assert.Contains("AI__Public__Kind", ex.Message);
        Assert.Contains("Gemini", ex.Message);
    }

    [Fact]
    public void AddAiServices_Throws_WhenThePublicKindIsMissing()
    {
        var config = Config();
        config.Remove("AI:Public:Kind");

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Public__Kind", ex.Message);
    }

    [Theory]
    [InlineData("AI:Public:Model", "AI__Public__Model")]
    [InlineData("AI:Public:ApiKey", "AI__Public__ApiKey")]
    [InlineData("AI:Private:Model", "AI__Private__Model")]
    [InlineData("AI:Private:BaseUrl", "AI__Private__BaseUrl")]
    [InlineData("AI:Rewrite:Model", "AI__Rewrite__Model")]
    [InlineData("AI:Rewrite:BaseUrl", "AI__Rewrite__BaseUrl")]
    public void AddAiServices_Throws_WhenARequiredValueIsBlank(string key, string expectedEnvVar)
    {
        var config = Config();
        config[key] = "   ";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains(expectedEnvVar, ex.Message);
    }

    [Fact]
    public void AddAiServices_Throws_WhenATimeoutIsNotPositive()
    {
        var config = Config();
        config["AI:Public:TimeoutSeconds"] = "0";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Public__TimeoutSeconds", ex.Message);
    }

    [Fact]
    public void AddAiServices_Throws_WhenABaseUrlIsNotAbsolute()
    {
        var config = Config();
        config["AI:Private:BaseUrl"] = "localhost:11434";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Private__BaseUrl", ex.Message);
    }

    // MedGemma authorises callers by IAM, so a Cloud Run BaseUrl without an identity token 403s on
    // every call. Digest and assessment code swallow per-member inference failures, so that 403
    // would read as "nothing was due" rather than an error — the misconfiguration has to be caught
    // at startup or it is invisible.
    [Theory]
    [InlineData("https://carditrack-dev-medgemma-abcdef.a.run.app")]
    [InlineData("https://carditrack-dev-medgemma-abcdef.a.RUN.APP")]
    public void AddAiServices_Throws_WhenACloudRunBaseUrlHasNoIdentityToken(string baseUrl)
    {
        var config = Config();
        config["AI:Private:BaseUrl"] = baseUrl;
        config["AI:Private:UseIdentityToken"] = "false";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Private__UseIdentityToken", ex.Message);
    }

    [Fact]
    public void AddAiServices_Succeeds_WhenACloudRunBaseUrlHasAnIdentityToken()
    {
        var config = Config();
        config["AI:Private:BaseUrl"] = "https://carditrack-dev-medgemma-abcdef.a.run.app";
        config["AI:Private:UseIdentityToken"] = "true";

        // Registration only — the handler mints a token lazily on first send, so nothing here
        // reaches the metadata server.
        Assert.NotNull(Resolve(config).GetRequiredService<IHttpClientFactory>());
    }

    // The reverse mistake: a bearer credential must never go out over plaintext.
    [Fact]
    public void AddAiServices_Throws_WhenAnIdentityTokenIsUsedOverPlainHttp()
    {
        var config = Config();
        config["AI:Private:BaseUrl"] = "http://localhost:11434";
        config["AI:Private:UseIdentityToken"] = "true";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Private__UseIdentityToken", ex.Message);
    }

    // The local default has to stay workable, or every developer turns the check off.
    [Fact]
    public void AddAiServices_Succeeds_ForALocalEndpointWithNoIdentityToken()
    {
        Assert.NotNull(Resolve(Config()).GetRequiredService<IHttpClientFactory>());
    }

    // The rewrite slot validates its own identity-token coherence independently of Private — same
    // rules, same reason (it lives on the same IAM-authorised Cloud Run host in every deployed
    // environment), separate config key.
    [Fact]
    public void AddAiServices_Throws_WhenARewriteCloudRunBaseUrlHasNoIdentityToken()
    {
        var config = Config();
        config["AI:Rewrite:BaseUrl"] = "https://carditrack-dev-medgemma-abcdef.a.run.app";
        config["AI:Rewrite:UseIdentityToken"] = "false";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Rewrite__UseIdentityToken", ex.Message);
    }

    [Fact]
    public void AddAiServices_Succeeds_WhenARewriteCloudRunBaseUrlHasAnIdentityToken()
    {
        var config = Config();
        config["AI:Rewrite:BaseUrl"] = "https://carditrack-dev-medgemma-abcdef.a.run.app";
        config["AI:Rewrite:UseIdentityToken"] = "true";

        Assert.NotNull(Resolve(config).GetRequiredService<IHttpClientFactory>());
    }

    [Fact]
    public void AddAiServices_Throws_WhenARewriteIdentityTokenIsUsedOverPlainHttp()
    {
        var config = Config();
        config["AI:Rewrite:BaseUrl"] = "http://localhost:11434";
        config["AI:Rewrite:UseIdentityToken"] = "true";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Rewrite__UseIdentityToken", ex.Message);
    }

    // The public slot's Vertex kind: no API key required — auth is IAM via ADC, so requiring a
    // key would force a fake secret into every Vertex deployment.
    [Fact]
    public void AddAiServices_ResolvesThePublicProviderToVertex_WithoutAnApiKey()
    {
        var config = Config();
        config["AI:Public:Kind"] = "VertexGemini";
        config["AI:Public:Model"] = "gemini-2.5-flash";
        config["AI:Public:ProjectId"] = "test-project";
        config["AI:Public:Location"] = "europe-west2";
        config.Remove("AI:Public:ApiKey");
        config["AI:Public:BaseUrl"] = string.Empty;

        var provider = Resolve(config);

        Assert.IsType<VertexAiClient>(provider.GetRequiredKeyedService<IExternalAiClient>("GeneralProvider"));
        Assert.Equal(
            new Uri("https://europe-west2-aiplatform.googleapis.com"),
            provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(AiServiceExtensions.PublicHttpClientName).BaseAddress);
    }

    [Fact]
    public void AddAiServices_Throws_WhenTheVertexPublicKindHasNoProject()
    {
        var config = Config();
        config["AI:Public:Kind"] = "VertexGemini";
        config["AI:Public:Location"] = "europe-west2";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Public__ProjectId", ex.Message);
    }

    // ApiKey stays required for the kinds that authenticate with one.
    [Fact]
    public void AddAiServices_StillRequiresTheApiKey_ForTheGeminiKind()
    {
        var config = Config();
        config.Remove("AI:Public:ApiKey");

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains("AI__Public__ApiKey", ex.Message);
    }

    private static Dictionary<string, string?> VertexRewriteConfig()
    {
        var config = Config();
        config["AI:Rewrite:Kind"] = "VertexGemini";
        config["AI:Rewrite:Model"] = "gemini-2.5-flash-lite";
        config["AI:Rewrite:ProjectId"] = "test-project";
        config["AI:Rewrite:Location"] = "europe-west2";
        config.Remove("AI:Rewrite:BaseUrl");
        return config;
    }

    /// <summary>
    /// A completion is drawn from the same window as the prompt, so a ceiling that meets or
    /// exceeds the window describes a call with no room to ask anything. Caught at boot because
    /// the symptom otherwise arrives per-generation and looks nothing like a config mistake: a
    /// reply cut off mid-token, reported as JSON that would not parse.
    /// </summary>
    [Theory]
    [InlineData("AI:Private")]
    [InlineData("AI:Rewrite")]
    public void AddAiServices_Throws_WhenTheOutputCeilingLeavesNoRoomForAPrompt(string section)
    {
        var config = Config();
        config[$"{section}:ContextTokens"] = "4096";
        config[$"{section}:MaxOutputTokens"] = "4096";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains($"{section}:MaxOutputTokens", ex.Message);
        Assert.Contains("ContextTokens", ex.Message);
    }

    [Theory]
    [InlineData("AI:Private", "ContextTokens")]
    [InlineData("AI:Private", "MaxOutputTokens")]
    [InlineData("AI:Rewrite", "ContextTokens")]
    [InlineData("AI:Rewrite", "MaxOutputTokens")]
    public void AddAiServices_Throws_WhenATokenBudgetIsNotPositive(string section, string key)
    {
        var config = Config();
        config[$"{section}:{key}"] = "0";

        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(config));

        Assert.Contains($"{section}:{key}", ex.Message);
    }

    /// <summary>
    /// The Vertex kind has no context window to configure — it is a property of the model, not a
    /// request parameter — so the key is neither required nor read on that branch. Both shapes a
    /// deployment can actually present are pinned: absent, and left behind at a value the Ollama
    /// kind would have refused to boot on.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    public void AddAiServices_IgnoresTheContextWindow_ForTheVertexRewriteKind(string? contextTokens)
    {
        var config = VertexRewriteConfig();
        if (contextTokens is not null)
            config["AI:Rewrite:ContextTokens"] = contextTokens;

        var client = Resolve(config).GetRequiredKeyedService<IExternalAiClient>("RewriteProvider");

        Assert.IsType<VertexAiClient>(client);
    }

    private static Dictionary<string, string?> Config() => new()
    {
        ["AI:Public:Kind"] = "Gemini",
        ["AI:Public:Model"] = "gemini-2.0-flash",
        ["AI:Public:ApiKey"] = "test-key",
        ["AI:Public:BaseUrl"] = "https://generativelanguage.googleapis.com",
        ["AI:Public:TimeoutSeconds"] = "60",
        ["AI:Public:MaxOutputTokens"] = "16000",
        ["AI:Private:Model"] = "medgemma",
        ["AI:Private:BaseUrl"] = "http://localhost:11434",
        ["AI:Private:TimeoutSeconds"] = "300",
        ["AI:Rewrite:Model"] = "gemma3:4b-it-qat",
        ["AI:Rewrite:BaseUrl"] = "http://localhost:11434",
        ["AI:Rewrite:TimeoutSeconds"] = "300"
    };

    private static ServiceProvider Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        // AddLogging: MedGemmaClient takes an ILogger from the container.
        return new ServiceCollection().AddLogging().AddAiServices(configuration).BuildServiceProvider();
    }
}
