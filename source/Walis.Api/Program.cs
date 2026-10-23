using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using Walis.Api.Endpoints;
using Walis.Api.Mcp;
using Walis.Api.Services;
using Walis.Api.Services.Ai;
using Walis.Core.Backup;
using Walis.Core.Database;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Plugins;
using Walis.Core.Repositories;
using Walis.Core.Security;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    // Ordinary JSON/API requests are capped at 1 MiB; the few large streaming endpoints such as artifacts raise it explicitly
    // through endpoint metadata and still enforce the byte budget again inside their own reader.
    options.Limits.MaxRequestBodySize = 1024 * 1024;
});

DapperConfiguration.Register();

var dataDirectory = DataDirectory.Resolve(builder.Environment, builder.Configuration);
PrivateFileSystem.EnsurePrivateDirectory(dataDirectory);
builder.Services.AddSingleton(new DataDirectory(dataDirectory));
builder.Services.AddSingleton<SetupCode>();
builder.Services.AddSingleton<BootstrapPolicy>();

// Secrets at rest (AI keys, secret variables, settings passwords, TOTP secrets, password parameters) are encrypted with one
// master key: Security:EncryptionKey when configured, otherwise a key generated once into <data directory>/secret.key.
// Only the source description goes into the container; the key itself stays inside the cipher.
var secretKey = SecretKeyStore.LoadOrCreate(builder.Configuration[SecretKeyStore.ConfigurationKey], dataDirectory);
builder.Services.AddSingleton(secretKey.Source);
builder.Services.AddSingleton<ISecretProtector>(new SecretProtector(secretKey.Value));
builder.Services.AddSingleton<StoredSecretScanner>();
var connectionString = builder.Configuration.GetConnectionString("Walis")
                       ?? $"Data Source={Path.Combine(dataDirectory, "walis.db")}";

// Database kind: inferred from the connection string when not configured, defaulting to single-node SQLite.
// Only PostgreSQL, MySQL and SQL Server let several API nodes share one dataset.
var databaseProvider = DatabaseProviders.Resolve(
    builder.Configuration["Database:Provider"], connectionString);
var node = new NodeIdentity(builder.Configuration["Database:Node"] ?? string.Empty, databaseProvider);

var corsOrigins = new CorsOriginStore();
var mcpAccess = new McpAccessStore();

builder.Services.AddSingleton(corsOrigins);
builder.Services.AddSingleton(mcpAccess);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(corsOrigins.IsAllowed)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// Every public route gets a coarse resource-exhaustion ceiling. Authentication and webhook
// endpoints add their own tighter limits; this protects otherwise cheap-looking list/log routes too.
// Requests per minute and source to sign-in, OTP and first-administrator setup (Security:AuthRateLimitPerMinute, default 30).
var authRateLimitPerMinute = int.TryParse(builder.Configuration["Security:AuthRateLimitPerMinute"], out var configuredAuthRate) &&
                             configuredAuthRate is >= 1 and <= 100_000
    ? configuredAuthRate
    : 30;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientAddressKey.For(context.Connection.RemoteIpAddress),
            static _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));
    options.AddPolicy("webhook-ingress", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientAddressKey.For(context.Connection.RemoteIpAddress),
            static _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));
    // Sign-in, OTP and first-administrator setup cost a PBKDF2 verification each and are anonymous: a much tighter ceiling
    // per source on top of the per-account throttle. A request without a peer address (in-process test host) is not counted.
    options.AddPolicy("auth-ingress", context =>
        context.Connection.RemoteIpAddress is null
            ? RateLimitPartition.GetNoLimiter("no-peer")
            : RateLimitPartition.GetFixedWindowLimiter(
                ClientAddressKey.For(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authRateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "請求頻率過高，請稍後再試。" },
            cancellationToken);
    };
});

// Notifications and plugins send requests to URLs from settings or manifests: never follow redirects,
// otherwise an allowed URL could redirect the request (with its secret headers) anywhere on the internal network.
// Administrator-configured destinations are also kept off private networks unless Security:OutboundAllowPrivateNetwork says otherwise.
var outboundAllowPrivateNetwork = OutboundHttpHandlers.AllowsPrivateNetwork(builder.Configuration);
builder.Services.AddHttpClient("notifications", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundHttpHandlers.CreateGuarded(8, outboundAllowPrivateNetwork));
builder.Services.AddHttpClient("ai", client => client.Timeout = TimeSpan.FromMinutes(3))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundHttpHandlers.CreateGuarded(8, outboundAllowPrivateNetwork));
builder.Services.AddHttpClient("backup", client => client.Timeout = TimeSpan.FromMinutes(30))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundHttpHandlers.CreateGuarded(4, outboundAllowPrivateNetwork));
builder.Services.AddHttpClient("plugins", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundHttpHandlers.CreatePlugin(8));

// Behind Caddy/nginx the real origin is in X-Forwarded-For. Only explicitly listed proxy IPs are accepted;
// trusting a whole private range would let any container on the same segment forge the origin and HTTPS state, bypassing rate limits and security checks.
var trustedProxyAddresses = (builder.Configuration["Security:TrustedProxies"] ?? string.Empty)
    .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(value => IPAddress.TryParse(value, out var address)
        ? address
        : throw new InvalidOperationException($"Security:TrustedProxies 包含無效的 IP 位址：{value}"))
    .Distinct()
    .ToArray();
var configuredForwardLimit = builder.Configuration["Security:ForwardLimit"];
var forwardLimit = string.IsNullOrWhiteSpace(configuredForwardLimit)
    ? 1
    : int.TryParse(configuredForwardLimit, out var parsedForwardLimit) && parsedForwardLimit is >= 1 and <= 3
        ? parsedForwardLimit
        : throw new InvalidOperationException("Security:ForwardLimit 必須是 1 到 3 的整數。");

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // With both trust lists empty the middleware would accept forwarded headers from anyone, so no listed proxy means the
    // headers are not processed at all.
    options.ForwardedHeaders = trustedProxyAddresses.Length == 0
        ? ForwardedHeaders.None
        : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = forwardLimit;

    // The framework trusts loopback by default. That would let any process on this machine, a build script included, put an
    // arbitrary address in X-Forwarded-For and walk past the MCP allowlist, per-key address limits and throttling. Only the
    // proxies the deployment lists are trusted; a same-machine Caddy or nginx is listed as 127.0.0.1 explicitly.
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
#pragma warning disable ASPDEPR005 // The obsolete KnownNetworks list still carries 127.0.0.0/8 by default and is still consulted.
    options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005

    foreach (var address in trustedProxyAddresses) options.KnownProxies.Add(address);
});

builder.Services.AddSingleton(node);
builder.Services.AddSingleton<IDbConnectionFactory>(_ =>
    DbConnectionFactory.Create(databaseProvider, connectionString));
// A database that has never stored settings gets its isolation default decided here, once: a Production server listening on
// a non-loopback address starts with "refuse to run without an isolation boundary" on, because anyone who can edit or run a
// job on an unisolated runner reads every secret. Settings already stored keep whatever they say.
var allowUnisolatedByDefault = BuildIsolationPolicy.AllowsUnisolatedBuildsByDefault(
    builder.Environment.IsProduction(),
    builder.Configuration["urls"] ?? builder.Configuration["ASPNETCORE_URLS"]);
builder.Services.AddSingleton<IDatabaseInitializer>(provider =>
    new DatabaseInitializer(provider.GetRequiredService<IDbConnectionFactory>())
    {
        InitialSettings = new WalisSettings { AllowUnisolatedBuilds = allowUnisolatedByDefault }
    });
builder.Services.AddSingleton<IJobRepository, JobRepository>();
builder.Services.AddSingleton<IRunnerRepository, RunnerRepository>();
builder.Services.AddSingleton<IEnvironmentVariableRepository, EnvironmentVariableRepository>();
builder.Services.AddSingleton<INotificationRepository, NotificationRepository>();
builder.Services.AddSingleton<ISettingsRepository, SettingsRepository>();
builder.Services.AddSingleton<ILocalizationRepository, LocalizationRepository>();
builder.Services.AddSingleton<ISecurityRepository, SecurityRepository>();
builder.Services.AddSingleton<IAuditRepository, AuditRepository>();
builder.Services.AddSingleton<IAiRepository, AiRepository>();
builder.Services.AddSingleton<ActionPackProvider>();
builder.Services.AddSingleton<Walis.Core.Actions.IActionPackSource>(provider => provider.GetRequiredService<ActionPackProvider>());
builder.Services.AddSingleton<IActionRepository, ActionRepository>();
builder.Services.AddSingleton<IBackupRepository, BackupRepository>();
builder.Services.AddSingleton<IApiKeyRepository, ApiKeyRepository>();
builder.Services.AddSingleton<IPluginRepository, PluginRepository>();
builder.Services.AddSingleton<ILeaseRepository, LeaseRepository>();
builder.Services.AddSingleton<RestoreService>();

builder.Services.AddSingleton<BuildQueue>();
builder.Services.AddSingleton<ShellCommandExecutor>();
builder.Services.AddSingleton<BuildLauncher>();
builder.Services.AddSingleton<BuildStepPlanner>();
builder.Services.AddSingleton<BuildIsolationPolicy>();

// Plugins: Core only knows the shape of IPluginCapabilities; the actual capabilities come from the API layer.
builder.Services.AddSingleton<IPluginCapabilities, PluginCapabilities>();
builder.Services.AddSingleton<PluginHost>();
builder.Services.AddSingleton<PluginActionRendererFactory>();

// MCP is mounted directly on the API: an AI agent only needs a URL and a key, nothing installed locally.
builder.Services
    .AddMcpServer(options => options.ServerInfo = new Implementation { Name = "Walis", Version = WalisVersion.Number })
    .WithHttpTransport()
    .WithTools<WalisMcpTools>();
builder.Services.AddSingleton<BuildStepRunner>();
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddSingleton<BuildCompletionService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<ArtifactStore>();
builder.Services.AddSingleton<RemoteArtifactUploadService>();
builder.Services.AddSingleton<PushWebhookService>();
builder.Services.AddSingleton<WebhookRequestGuard>();
builder.Services.AddSingleton<SecurityMetrics>();
builder.Services.AddSingleton<SecurityEventService>();
builder.Services.AddSingleton<ISignInLockoutSink>(provider => provider.GetRequiredService<SecurityEventService>());
builder.Services.AddSingleton<IReauthentication, Reauthentication>();
builder.Services.AddSingleton(provider => new SignInThrottle(provider.GetRequiredService<IDbConnectionFactory>())
{
    LockoutSink = provider.GetRequiredService<ISignInLockoutSink>()
});
builder.Services.AddSingleton<AiService>();
builder.Services.AddSingleton<AuditService>();

// Backups: the upload implementation for each storage provider is chosen by the factory from the Provider.
builder.Services.AddSingleton<IBackupHttpClientFactory, BackupHttpClientFactory>();
builder.Services.AddSingleton<IBackupTargetClient, LocalTargetClient>();
builder.Services.AddSingleton<IBackupTargetClient, S3TargetClient>();
builder.Services.AddSingleton<IBackupTargetClient, DropboxTargetClient>();
builder.Services.AddSingleton<IBackupTargetClient, GoogleDriveTargetClient>();
builder.Services.AddSingleton<IBackupTargetClient, OneDriveTargetClient>();
builder.Services.AddSingleton<BackupTargetClientFactory>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddHostedService<BuildExecutionWorker>();
builder.Services.AddHostedService<SchedulerWorker>();
builder.Services.AddHostedService<BackupWorker>();

var app = builder.Build();

// The configuration providers copied the deployment secrets above; the environment copy would only let a build under the
// same account read them back (ps -E, /proc/<pid>/environ).
SensitiveEnvironment.Clear();

await app.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

// Say once where the key lives, and say loudly when stored secrets do not open with it: that is what a restore on another
// machine without secret.key looks like, and the admin fixes it by copying the key or by clearing the values in the settings page.
var keyLocation = secretKey.Source.FilePath is { } keyPath
    ? $"{secretKey.Source.Description}（{keyPath}）"
    : secretKey.Source.Description;
app.Logger.LogInformation(
    secretKey.Source.Generated
        ? "機密加密金鑰已產生：{Location}。備份還原到其他機器時要帶著這個檔案，或改設 Security:EncryptionKey。"
        : "機密加密金鑰來源：{Location}",
    keyLocation);

try
{
    var undecryptable = await app.Services.GetRequiredService<StoredSecretScanner>().ScanAsync();

    if (undecryptable.Count != 0)
        app.Logger.LogError(
            "有 {Count} 筆已加密的機密無法用目前的金鑰解密（{Items}）。這通常是備份還原到沒有原本 secret.key 的機器，或 Security:EncryptionKey 改了。" +
            "請把原本的金鑰放回去，或在「設定 → 安全」清除無法解密的機密後重新填寫。",
            undecryptable.Count,
            string.Join("、", undecryptable.Undecryptable.Take(10).Select(item => $"{item.Table}:{item.Item}")));
}
catch (Exception exception)
{
    app.Logger.LogWarning(exception, "啟動時檢查機密加密狀態失敗");
}

var settingsRepository = app.Services.GetRequiredService<ISettingsRepository>();

if (!(await settingsRepository.GetAsync()).AllowUnisolatedBuilds && !BuildIsolation.IsSandboxAvailable)
    app.Logger.LogWarning(
        "「沒有隔離邊界時拒絕執行」已啟用，而這個平台沒有 Walis 能套用的沙箱：內建執行器不會執行建置，" +
        "直到管理員在「系統設定 → 建置」明確接受未隔離建置的風險，或把任務指派給已置於外部隔離的執行器。");

// A fresh install can be opened from another machine before anyone owns it. Until the first administrator exists, setting up
// from anywhere but this machine needs a code only the installer can read. A deployment-provided token takes its place.
var setupCode = app.Services.GetRequiredService<SetupCode>();
var hasAnyUser = await app.Services.GetRequiredService<ISecurityRepository>().HasAnyUserAsync();

// The bound addresses are only final once the server has started. Reachable from other machines (or Security:RequireSetupCode set)
// and still without an administrator means the setup code is what protects the installation: say so where the installer looks.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var bootstrap = app.Services.GetRequiredService<BootstrapPolicy>();
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                    ?? [];
    bootstrap.Observe(addresses);

    if (!hasAnyUser && (bootstrap.ListensOnNonLoopback || bootstrap.RequireSetupCode))
        app.Logger.LogInformation(
            "首次設定需要設定碼：伺服器監聽 {Addresses}，{Reason}。",
            string.Join(", ", addresses),
            bootstrap.RequireSetupCodeByDefault
                ? "正式環境預設本機也要輸入設定碼（Security:RequireSetupCode=false 可改回本機免碼）"
                : bootstrap.RequireSetupCode
                ? "已設定 Security:RequireSetupCode，本機也要輸入設定碼"
                : "其他機器也連得到；本機免碼只接受沒有轉送標頭、Host 連接埠等於監聽埠的請求");
});

if (hasAnyUser)
{
    setupCode.Clear();
}
else if (string.IsNullOrWhiteSpace(app.Configuration["Security:BootstrapToken"]))
{
    app.Logger.LogInformation(
        """

        ************************************************************
        Walis 還沒有管理員。開啟管理介面建立第一個管理員時，
        設定畫面會要求輸入這組設定碼（開發環境在本機可免輸入）：
        No administrator yet. When you create the first one in the UI,
        the setup screen asks for this code (not needed on this machine in Development):

            {SetupCode}

        設定碼也存在 / also saved in: {SetupCodePath}
        ************************************************************
        """,
        setupCode.EnsureCreated(),
        setupCode.FilePath);
}

if (databaseProvider == DatabaseProvider.Sqlite)
{
    var sqliteOptions = new SqliteConnectionStringBuilder(connectionString);

    if (!string.IsNullOrWhiteSpace(sqliteOptions.DataSource) &&
        sqliteOptions.Mode != SqliteOpenMode.Memory &&
        !string.Equals(sqliteOptions.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
    {
        var sqlitePath = Path.GetFullPath(sqliteOptions.DataSource);

        if (File.Exists(sqlitePath)) PrivateFileSystem.RestrictFile(sqlitePath);
    }
}

var startupSettings = await settingsRepository.GetAsync();
corsOrigins.Update(startupSettings.CorsOrigins);
mcpAccess.Update(startupSettings.McpAllowedAddresses);

app.UseForwardedHeaders();
app.UseRateLimiter();

// Plain HTTP is accepted from anywhere by default, so a fresh install can be opened from another machine straight away.
// A deployment that sets Security:RequireHttps gets the strict rule: a remote API request must arrive over HTTPS (or through
// a trusted reverse proxy that terminates TLS). Local HTTP is kept for single-node maintenance; Test/Development never force TLS.
var requireHttps = SessionCookieService.RequiresHttps(app.Configuration);

app.Use(async (context, next) =>
{
    var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
    var externallySecure = SessionCookieService.IsExternallySecure(context);
    var remote = context.Connection.RemoteIpAddress;
    var protectedPath = context.Request.Path.StartsWithSegments("/api") ||
                        context.Request.Path.StartsWithSegments("/mcp") ||
                        context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase);

    if (requireHttps && protectedPath && !externallySecure &&
        !environment.IsDevelopment() && !environment.IsEnvironment("Test") &&
        remote is not null && !IPAddress.IsLoopback(remote))
    {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        await context.Response.WriteAsJsonAsync(new { message = "此伺服器已設定 Security:RequireHttps，遠端 API 連線必須使用 HTTPS。" });
        return;
    }

    if (externallySecure && !environment.IsDevelopment() && !environment.IsEnvironment("Test"))
        context.Response.Headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    await next();
});

app.UseCors();

// Basic security headers. When the admin UI shares the origin with the API, CSP is sent too: the UI is Vite-bundled static files
// with no inline scripts; styles allow unsafe-inline only because CoreUI uses inline style.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; script-src-attr 'none'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; " +
        "frame-ancestors 'none'; frame-src 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";
    if (context.Request.Path == "/" ||
        context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
        headers.CacheControl = "no-store";
    await next();
});

// RequestSizeLimitAttribute limits the stream on Kestrel, but TestServer/custom servers do not necessarily implement
// IHttpMaxRequestBodySizeFeature. Fail fast on Content-Length first and set the feature as well, so the endpoint's declared cap applies
// before JSON model binding; without Content-Length the server's streaming limit still guards.
app.Use(async (context, next) =>
{
    var maximum = context.GetEndpoint()?.Metadata
        .GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;

    if (maximum is { } bytes)
    {
        if (context.Request.ContentLength is { } length && length > bytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { message = $"請求內容不可超過 {bytes:N0} bytes。" });
            return;
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false } && feature.MaxRequestBodySize != bytes)
            // Endpoint metadata may intentionally be either tighter (ordinary JSON) or larger
            // (the bounded streaming artifact upload) than Kestrel's 1 MiB default.
            feature.MaxRequestBodySize = bytes;
    }

    await next();
});

// When deployed as a single service the admin UI's build output sits in wwwroot on the same origin as the API;
// during development (no wwwroot) the pure-API behavior is kept.
var hasAdminUi = File.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html"));

if (hasAdminUi)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseMiddleware<AuthenticationMiddleware>();

if (!hasAdminUi)
    app.MapGet("/", () => Results.Ok(new
    {
        service = "Walis API",
        purpose = "精簡 CI/CD 任務管理",
        version = WalisVersion.Display
    }));

// The health check also reports the database and node: in a multi-node deployment this shows which node you reached and what it is called.
app.MapGet("/health", (HttpContext context) =>
{
    if (RequestAccess.Role(context) == UserRole.Admin)
        return Results.Json(new
        {
            status = "ok",
            database = DatabaseProviders.DisplayName(databaseProvider),
            node = node.Name,
            clustered = node.IsClustered
        });

    return Results.Json(new { status = "ok" });
});

app.MapDashboardEndpoints();
app.MapJobEndpoints();
app.MapBuildEndpoints();
app.MapRunnerEndpoints();
app.MapEnvironmentEndpoints();
app.MapNotificationEndpoints();
app.MapSettingsEndpoints();
app.MapLocalizationEndpoints();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapAuditEndpoints();
app.MapSecurityCenterEndpoints();
app.MapWebhookEndpoints();
app.MapAiEndpoints();
app.MapActionEndpoints();
app.MapBackupEndpoints();
app.MapApiKeyEndpoints();
app.MapPortabilityEndpoints();
app.MapPluginEndpoints();
app.MapMetricsEndpoints();
app.MapMcp("/mcp");

app.Run();

public partial class Program;