namespace CardiTrack.Shared;

public static class ConfigurationKeys
{
    public static class ConnectionStrings
    {
        public const string DefaultConnection = "ConnectionStrings:DefaultConnection";
        public const string Redis = "ConnectionStrings:Redis";
    }

    public static class Auth0
    {
        public const string Domain = "Auth0:Domain";
        public const string Audience = "Auth0:Audience";
        public const string ClientId = "Auth0:ClientId";
        public const string ClientSecret = "Auth0:ClientSecret";
        public const string CallbackUrl = "Auth0:CallbackUrl";
        public const string LogoutUrl = "Auth0:LogoutUrl";
    }

    public static class Redis
    {
        /// <summary>
        /// PEM bundle of the Memorystore instance's certificate authorities, injected from
        /// Secret Manager. Set only where the cache runs with in-transit encryption; empty
        /// locally, where docker-compose speaks plain Redis.
        /// </summary>
        public const string CaCertificate = "Redis:CaCertificate";
    }

    public static class Cors
    {
        public const string AllowedOrigins = "Cors:AllowedOrigins";
    }

    public static class Apm
    {
        /// <summary>
        /// Selector for APM provider injection — the value names an engine in
        /// ApmProviderRegistry (e.g. "BetterStack"); empty disables shipping.
        /// Read via ApmExtensions.LoadEngine. Apm:Data stays options-bound (it may
        /// arrive as one JSON env var): see ApmExtensions.GetApmOptions.
        /// </summary>
        public const string Engine = "Apm:Engine";
    }

    public static class DataProtection
    {
        /// <summary>Directory persisting the ASP.NET Data Protection key ring — a GCS-backed volume on Cloud Run; unset locally, falling back to the default container-local store.</summary>
        public const string KeysPath = "DataProtection:KeysPath";
    }

    public static class Encryption
    {
        /// <summary>Base64-encoded 256-bit AES key. No IV key exists — AES-GCM derives a fresh nonce per operation.</summary>
        public const string Key = "Encryption:Key";
    }

    public static class Notifications
    {
        /// <summary>
        /// Base64-encoded 256-bit HMAC key signing ack/fetch tokens (notification_engine.md §7.2
        /// C3/C5). Only ever injected into API and Worker — the AI pipeline never sends push
        /// directly, so it has no reason to hold this key.
        /// </summary>
        public const string AckTokenKey = "Notifications:AckTokenKey";
    }

    /// <summary>
    /// The GCP AI pipeline's identity, as seen from the API's internal enqueue endpoint (§7.2 C4).
    /// Audience-pinning alone admits any GCP principal, so the endpoint also pins the calling
    /// service account's verified email — both values come from here, not a hardcoded string.
    /// </summary>
    public static class Pipeline
    {
        /// <summary>The OIDC audience the pipeline's ID token is minted for.</summary>
        public const string Audience = "Pipeline:Audience";

        /// <summary>The pipeline's service account email — the only caller <see cref="Audience"/> tokens are accepted from.</summary>
        public const string ServiceAccount = "Pipeline:ServiceAccount";
    }

    public static class Health
    {
        public const string Token = "Health:Token";
    }

    /// <summary>
    /// Local-development affordances. Everything here is absent-by-default: no key is declared in
    /// any appsettings.json and none is injected by Terraform, so the surfaces these unlock exist
    /// only on a machine that opted in explicitly (user-secrets or an environment variable).
    /// </summary>
    public static class Dev
    {
        /// <summary>
        /// Base64-encoded 256-bit HMAC key authorizing the dev-only test-push endpoint
        /// (notification_engine.md §13). Its absence is the endpoint's primary off switch — the
        /// route is never mapped without it — which is why, unlike every other key here, it must
        /// never be given a placeholder value in configuration: a placeholder would turn the
        /// endpoint on everywhere with a publicly-known key.
        /// </summary>
        public const string PushTokenKey = "Dev:PushTokenKey";
    }

    public static class Webhook
    {
        /// <summary>
        /// The full Authorization header value Google sends with every webhook notification —
        /// including its scheme — as registered in the Subscriber's endpointAuthorization.secret.
        /// The receiver compares the whole header against this, constant-time.
        /// </summary>
        public const string Secret = "Webhook:Secret";
    }

    public static class PubSub
    {
        public const string ProjectId = "PubSub:ProjectId";
        public const string TopicId = "PubSub:TopicId";
        public const string SubscriptionId = "PubSub:SubscriptionId";
    }

    public static class CloudRun
    {
        /// <summary>Bare env var injected by Cloud Run (no section) — the port to listen on.</summary>
        public const string Port = "PORT";
    }

    public static class Gcp
    {
        /// <summary>
        /// Bare env var (no section), injected identically into every Cloud Run service by
        /// <c>infrastructure/main.tf</c>. Passed explicitly into <c>FirebaseAdmin.AppOptions</c>
        /// when constructing the push-send <c>FirebaseApp</c> (PushServiceExtensions) — ADC alone
        /// resolves a *credential* via the metadata server on Cloud Run, but the .NET Firebase Admin
        /// SDK does not query the metadata server for the *project ID* itself; left implicit, that
        /// resolution is a cold-start race that fails intermittently on a fresh instance and, because
        /// Worker's BackgroundServiceExceptionBehavior is StopHost, turns into a permanent crash loop.
        /// </summary>
        public const string ProjectId = "GCP_PROJECT_ID";
    }

    public static class Deployment
    {
        /// <summary>
        /// Bare env var (no section) overriding the version baked into the image at build
        /// time. Normally unset: the release version is stamped into the assembly by the
        /// Dockerfile's VERSION build arg, which CI feeds from the deploy's tag without its
        /// leading "v". Read via DeploymentInfo, which runs before configuration exists —
        /// hence no section.
        /// </summary>
        public const string Version = "DEPLOY_VERSION";

        /// <summary>
        /// Bare env var (no section) overriding the environment reported on telemetry.
        /// Normally unset — the environment comes from <see cref="AspNetCoreEnvironment"/>
        /// below. This exists for the case where telemetry should be labelled differently
        /// from the name that selects appsettings files, so relabelling one does not
        /// silently repoint the other.
        /// </summary>
        public const string Environment = "DEPLOY_ENVIRONMENT";

        /// <summary>
        /// The standard ASP.NET Core environment variable, which Terraform sets per
        /// environment ("Dev" / "Prod" — note it is not .NET's "Development" /
        /// "Production", so deployed hosts all run production-like config). Read as a raw
        /// env var rather than through IHostEnvironment on purpose: IHostEnvironment
        /// substitutes "Production" when nothing is set, and a machine that never said
        /// which environment it is should report none, not prod.
        /// </summary>
        public const string AspNetCoreEnvironment = "ASPNETCORE_ENVIRONMENT";
    }

    /// <summary>Section — options-bound via IConfiguration.GetSection(), not ConfigurationLoader.Get().</summary>
    public static class IpRateLimiting
    {
        public const string SectionName = "IpRateLimiting";
    }

    public static class Workers
    {
        public static class WearableSyncWorker
        {
            public const string CronExpression = "Workers:WearableSyncWorker:CronExpression";
        }
    }

    public static class Api
    {
        public const string BaseUrl = "Api:BaseUrl";
    }

    /// <summary>Array section — use with IConfiguration.GetSection(), not ConfigurationLoader.Get().</summary>
    public static class DeviceProviders
    {
        public const string SectionName = "DeviceProviders";
    }

    public static class AI
    {
        /// <summary>
        /// Swappable off-estate provider for reports and chat — kind, model, key, optional URL.
        /// Object section: use with IConfiguration.GetSection(), not ConfigurationLoader.Get().
        /// </summary>
        public const string PublicSectionName = "AI:Public";

        /// <summary>
        /// Self-hosted MedGemma used for medical analysis. Carries where it lives and which weights
        /// it serves — never which provider to use, which is fixed in code.
        /// Object section: use with IConfiguration.GetSection(), not ConfigurationLoader.Get().
        /// </summary>
        public const string PrivateSectionName = "AI:Private";

        /// <summary>
        /// Self-hosted, non-medical model used only to rewrite MedGemma's clinical output into
        /// caregiver language. Same in-project guarantee as <see cref="PrivateSectionName"/> — no
        /// provider kind, fixed in code.
        /// Object section: use with IConfiguration.GetSection(), not ConfigurationLoader.Get().
        /// </summary>
        public const string RewriteSectionName = "AI:Rewrite";
    }

    /// <summary>
    /// The environmental-context enrichment feature's Google Maps Platform credentials. Read
    /// only by <c>CardiTrack.PipelineJobs</c>'s <c>enrich</c> job — see
    /// <c>EnvironmentalServiceExtensions</c>.
    /// </summary>
    public static class Environmental
    {
        /// Object section: use with IConfiguration.GetSection(), not ConfigurationLoader.Get().
        public const string SectionName = "Environmental";
    }

    /// <summary>Blob storage for user-supplied binary content — today, member profile photos.</summary>
    public static class Storage
    {
        /// <summary>
        /// Member profile photo storage (bucket name, signed-URL TTLs, upload cap) — see
        /// <c>StorageServiceExtensions</c>. The bucket arrives from Terraform as
        /// <c>Storage__MemberPhotos__Bucket</c>; empty (every local machine) disables the
        /// feature rather than failing startup.
        /// Object section: use with IConfiguration.GetSection(), not ConfigurationLoader.Get().
        /// </summary>
        public const string MemberPhotosSectionName = "Storage:MemberPhotos";
    }
}
