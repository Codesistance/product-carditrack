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

    public static class Health
    {
        public const string Token = "Health:Token";
    }

    public static class CloudRun
    {
        /// <summary>Bare env var injected by Cloud Run (no section) — the port to listen on.</summary>
        public const string Port = "PORT";
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
        /// <summary>Name of the active provider for generative tasks (reports, chat). E.g. "Gemini"</summary>
        public const string GeneralProvider = "AI:GeneralProvider";

        /// <summary>Name of the active provider for medical analysis tasks. E.g. "MedGemma"</summary>
        public const string MedicalProvider = "AI:MedicalProvider";

        /// <summary>Array section — use with IConfiguration.GetSection(), not ConfigurationLoader.Get().</summary>
        public const string ProvidersSectionName = "AI:Providers";
    }
}
