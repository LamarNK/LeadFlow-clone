using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Orbita.Api.Auth;
using Orbita.Api.Data;
using Orbita.Api.Hubs;
using Orbita.Api.Models;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;
using Orbita.Logging.Audit;
using StackExchange.Redis;
using System.Threading.RateLimiting;

namespace Orbita.Api.Endpoints;

public static class OrbitaApiStartupExtensions
{
    public static void ConfigureOrbitaApi(this WebApplicationBuilder builder)
    {
        builder.AddOrbitaLogging("Orbita.Api");

        const long defaultMaxUploadBytes = 536_870_912;
        var maxUploadBytes = builder.Configuration.GetValue<long?>("WorkerReleases:MaxUploadBytes") ?? defaultMaxUploadBytes;

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = maxUploadBytes;
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MinResponseDataRate = null;
        });

        builder.Services.AddDbContext<OrbitaDbContext>(options =>
        {
            options.UseNpgsql(builder.Configuration.GetConnectionString("Default"));
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });

        builder.Services
            .AddIdentity<IdentityUser, IdentityRole>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 8;
                options.User.RequireUniqueEmail = true;
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<OrbitaDbContext>()
            .AddDefaultTokenProviders();

        var jwtKey = builder.Configuration["Jwt:Key"] ?? "OrbitaDevSigningKey_ChangeInProduction_32chars!";
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "Orbita",
                    ValidAudience = builder.Configuration["Jwt:Audience"] ?? "Orbita.Web",
                    IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtKey)),
                    RoleClaimType = ClaimTypes.Role,
                    NameClaimType = ClaimTypes.NameIdentifier
                };
                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = async context =>
                    {
                        var message = context.Exception.Message;
                        if (message.Contains("IDX14100", StringComparison.Ordinal)
                            || message.Contains("no dots", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        await GlobalLogger.Instance.LogAsync(
                            $"JWT authentication failed: {message}",
                            DeskLinkAuditLogLevel.Warning,
                            errorKey: "auth.jwt.failed",
                            properties: new Dictionary<string, object?>
                            {
                                ["http.path"] = context.HttpContext.Request.Path.Value
                            });
                    },
                    OnChallenge = async context =>
                    {
                        if (context.AuthenticateFailure is not null)
                        {
                            return;
                        }

                        await GlobalLogger.Instance.LogAsync(
                            $"JWT challenge: missing or invalid token for {context.Request.Method} {context.Request.Path}.",
                            DeskLinkAuditLogLevel.Warning,
                            errorKey: "auth.jwt.challenge",
                            properties: new Dictionary<string, object?>
                            {
                                ["http.path"] = context.Request.Path.Value
                            });
                    },
                    OnTokenValidated = async context =>
                    {
                        var userManager = context.HttpContext.RequestServices
                            .GetRequiredService<UserManager<IdentityUser>>();
                        await JwtSecurityStampValidator.ValidateAsync(context, userManager);
                    },
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken)
                            && accessToken.ToString().Contains('.', StringComparison.Ordinal)
                            && (path.StartsWithSegments("/hubs/panel", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWithSegments("/hubs/captcha", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWithSegments("/hubs/worker", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWithSegments("/hubs/browser-monitor", StringComparison.OrdinalIgnoreCase)))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            })
            .AddScheme<AuthenticationSchemeOptions, WorkerApiKeyAuthenticationHandler>(
                WorkerApiKeyAuthenticationHandler.SchemeName,
                _ => { });

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("Worker", policy =>
            {
                policy.AddAuthenticationSchemes(WorkerApiKeyAuthenticationHandler.SchemeName);
                policy.RequireAuthenticatedUser();
            });
            options.AddPolicy("Panel", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(PanelPermissions.ClaimType);
            });
            options.AddPolicy("Admin", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireClaim(PanelPermissions.ClaimType, PanelPermissions.Administration);
            });
            options.AddPolicy("OfficeStaff", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireRole(PanelRoles.Admin, PanelRoles.OfficeLead);
            });
            foreach (var permission in PanelPermissions.All)
            {
                options.AddPolicy(permission.Id, policy =>
                {
                    policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                    if (permission.Id is PanelPermissions.CrmBoard or PanelPermissions.CrmTasks or PanelPermissions.CrmAnalytics)
                    {
                        policy.RequireAssertion(context =>
                            context.User.HasClaim(PanelPermissions.ClaimType, permission.Id)
                            || context.User.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Crm)
                            || (permission.Id is PanelPermissions.CrmBoard or PanelPermissions.CrmTasks
                                && context.User.HasClaim(PanelPermissions.ClaimType, PanelPermissions.CrmTeam)));
                    }
                    else if (permission.Id == PanelPermissions.Listings)
                    {
                        policy.RequireAssertion(context =>
                            context.User.HasClaim(PanelPermissions.ClaimType, PanelPermissions.Listings)
                            || context.User.IsInRole(PanelRoles.Admin)
                            || context.User.IsInRole(PanelRoles.Operator));
                    }
                    else
                    {
                        policy.RequireClaim(PanelPermissions.ClaimType, permission.Id);
                    }
                });
            }
            options.AddPolicy("WorkerOrPanel", policy =>
            {
                policy.AddAuthenticationSchemes(
                    JwtBearerDefaults.AuthenticationScheme,
                    WorkerApiKeyAuthenticationHandler.SchemeName);
                policy.RequireAuthenticatedUser();
            });
        });
        builder.Services.Configure<OrbitaCacheOptions>(builder.Configuration.GetSection(OrbitaCacheOptions.SectionName));
        builder.Services.AddMemoryCache();
        var redisConfiguration = builder.Configuration["Cache:RedisConfiguration"];
        if (!string.IsNullOrWhiteSpace(redisConfiguration))
        {
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConfiguration;
                options.InstanceName = builder.Configuration["Cache:InstanceName"] ?? "orbita:";
            });
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var configuration = ConfigurationOptions.Parse(redisConfiguration);
                configuration.AbortOnConnectFail = false;
                configuration.ConnectRetry = 1;
                configuration.ConnectTimeout = 1_000;
                configuration.SyncTimeout = 1_000;
                return ConnectionMultiplexer.Connect(configuration);
            });
        }
        else
        {
            builder.Services.AddDistributedMemoryCache();
        }

        builder.Services.AddHybridCache(options =>
        {
            // Files and unbounded payloads never use this layer. One worker's
            // account graph can expand beyond two MiB while serializing. Those
            // entries use distributed-only LargeRealtime and never enter API L1.
            options.MaximumPayloadBytes = 8 * 1024 * 1024;
        });
        builder.Services.AddSingleton<IOrbitaQueryCache, OrbitaQueryCache>();
        builder.Services.AddHostedService<RedisCacheInvalidationListener>();

        builder.Services.AddOpenApi();
        var signalR = builder.Services.AddSignalR(options =>
            {
                // MHTML captcha snapshots are far above the 32 KB default.
                options.MaximumReceiveMessageSize = 16 * 1024 * 1024;
                options.EnableDetailedErrors = builder.Environment.IsDevelopment();
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            });
        if (!string.IsNullOrWhiteSpace(redisConfiguration))
        {
            signalR.AddStackExchangeRedis(redisConfiguration);
        }
        builder.Services.AddSingleton<IPanelRealtimeNotifier, PanelRealtimeNotifier>();
        builder.Services.AddSingleton<WorkerConnectionRegistry>();
        builder.Services.AddSingleton<IWorkerPushNotifier, WorkerPushNotifier>();
        builder.Services.AddSingleton<CaptchaRelayRegistry>();
        builder.Services.AddSingleton<BrowserMonitorRegistry>();
        builder.Services.AddSingleton<BrowserMonitorService>();
        builder.Services.AddSingleton<LocalChromeLoginSessionService>();
        builder.Services.AddSingleton<ICaptchaSessionRelayNotifier, CaptchaSessionRelayNotifier>();
        builder.Services.AddScoped<CaptchaSessionService>();
        builder.Services.AddScoped<TopUpSessionService>();
        builder.Services.AddSingleton<ICaptchaLockNotifier, CaptchaLockNotifier>();
        builder.Services.AddHostedService<CaptchaSessionSweeperService>();
        builder.Services.AddHostedService<TopUpSessionSweeperService>();
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("Web", policy =>
                policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["https://localhost:7123", "http://localhost:5123"])
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials());
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(
                WorkerReleasePublishEndpoints.RateLimitPolicyName,
                context =>
                {
                    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter(
                        remoteIp,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        });
                });
            options.AddPolicy(
                BitrixWorkforceEndpoints.RateLimitPolicyName,
                context =>
                {
                    var receiver = context.Request.RouteValues["publicId"]?.ToString() ?? "unknown";
                    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"{receiver}:{remoteIp}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 300,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        });
                });
        });

        builder.Services.AddScoped<TelemetryService>();
        builder.Services.AddScoped<WorkerSnapshotRetentionPruner>();
        builder.Services.AddHostedService<WorkerSnapshotRetentionService>();
        builder.Services.AddScoped<DashboardQueryService>();
        builder.Services.AddScoped<AvitoAdsSyncService>();
        builder.Services.AddScoped<AvitoAdsQueryService>();
        builder.Services.AddScoped<OfficeStatisticsQueryService>();
        builder.Services.AddScoped<PanelAuditService>();
        builder.Services.AddScoped<AccessProfileService>();
        builder.Services.AddScoped<PanelUserService>();
        builder.Services.AddScoped<OfficeStaffService>();
        builder.Services.AddScoped<OfficeScopeService>();
        builder.Services.AddScoped<OfficeAdminService>();
        builder.Services.AddScoped<WorkerAdminService>();
        builder.Services.AddScoped<WorkerConfigService>();
        builder.Services.AddScoped<WorkerSettingsTemplateService>();
        builder.Services.AddHttpClient<IMultiloginAutomationTokenIssuer, MultiloginAutomationTokenIssuer>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .RemoveAllLoggers();
        builder.Services.AddScoped<WorkerScheduleService>();
        builder.Services.AddScoped<WorkerCommandService>();
        builder.Services.AddScoped<WorkerEventService>();
        builder.Services.Configure<WorkerReleaseOptions>(builder.Configuration.GetSection(WorkerReleaseOptions.SectionName));
        builder.Services.Configure<WorkerDiagnosticsOptions>(builder.Configuration.GetSection(WorkerDiagnosticsOptions.SectionName));
        builder.Services.Configure<CrmTaskAttachmentOptions>(builder.Configuration.GetSection(CrmTaskAttachmentOptions.SectionName));
        builder.Services.Configure<CrmSuccessDocumentOptions>(builder.Configuration.GetSection(CrmSuccessDocumentOptions.SectionName));
        builder.Services.Configure<CrmCallRecordingOptions>(builder.Configuration.GetSection(CrmCallRecordingOptions.SectionName));
        builder.Services.Configure<CerioAiOptions>(builder.Configuration.GetSection(CerioAiOptions.SectionName));
        builder.Services.Configure<CrmTelephonyWebRtcOptions>(builder.Configuration.GetSection(CrmTelephonyWebRtcOptions.SectionName));
        builder.Services.Configure<CrmSipRuntimeOptions>(builder.Configuration.GetSection(CrmSipRuntimeOptions.SectionName));
        builder.Services.Configure<CrmDeadlineNotificationOptions>(builder.Configuration.GetSection(CrmDeadlineNotificationOptions.SectionName));
        builder.Services.Configure<CrmAnalyticsOptions>(builder.Configuration.GetSection(CrmAnalyticsOptions.SectionName));
        builder.Services.Configure<CrmReprocessingOptions>(builder.Configuration.GetSection(CrmReprocessingOptions.SectionName));
        builder.Services.Configure<ServiceLogsOptions>(builder.Configuration.GetSection(ServiceLogsOptions.SectionName));
        builder.Services.AddSingleton<WorkerReleaseService>();
        builder.Services.AddScoped<WorkerDiagnosticsService>();
        builder.Services.AddScoped<CrmTaskAttachmentStorageService>();
        builder.Services.AddScoped<CrmSuccessDocumentStorageService>();
        builder.Services.AddScoped<CrmCallRecordingStorageService>();
        builder.Services.AddHostedService<WorkerDiagnosticsCleanupService>();
        builder.Services.AddSingleton<WorkerLogFileArchive>();
        builder.Services.AddScoped<WorkerLogArchiveService>();
        builder.Services.AddScoped<MonitoringRunIngestService>();
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = maxUploadBytes;
            options.ValueLengthLimit = int.MaxValue;
            options.MultipartHeadersLengthLimit = int.MaxValue;
        });
        builder.Services.AddScoped<CrmLeadDistributionService>();
        builder.Services.AddScoped<CrmWorkspaceService>();
        builder.Services.AddScoped<CrmReprocessingService>();
        builder.Services.AddHostedService<CrmReprocessingHostedService>();
        builder.Services.AddScoped<CrmTelephonyService>();
        builder.Services.AddScoped<CrmMissedCallsQueryService>();
        builder.Services.AddScoped<CrmTelephonyProviderAccountService>();
        builder.Services.AddScoped<CrmTelephonyCredentialProtector>();
        builder.Services.AddSingleton<CrmSipRuntimeConfigWriter>();
        builder.Services.AddScoped<PlusofonRecordingSyncService>();
        builder.Services.AddScoped<TelephonyProviderAccountSyncService>();
        builder.Services.AddScoped<ProviderRecordingArchiveService>();
        builder.Services.AddScoped<CrmCallAiProcessingService>();
        builder.Services.AddSingleton<ICerioAiClient, CerioAiClient>();
        builder.Services.AddSingleton<IPlusofonApiClient, PlusofonApiClient>();
        builder.Services.AddScoped<CrmDeadlineNotificationService>();
        builder.Services.AddSingleton<ICrmNotificationRealtimeNotifier, CrmNotificationRealtimeNotifier>();
        builder.Services.AddScoped<CrmAnalyticsQueryService>();
        builder.Services.AddScoped<BitrixInstanceService>();
        builder.Services.AddScoped<BitrixCrmImportService>();
        builder.Services.AddSingleton<BitrixCrmExportParser>();
        builder.Services.AddSingleton<BitrixCrmImportTokenProtector>();
        builder.Services.AddScoped<BitrixWorkforceSettingsService>();
        builder.Services.AddScoped<BitrixWorkforceEventReceiver>();
        builder.Services.AddScoped<BitrixWorkforceProcessor>();
        builder.Services.AddScoped<DistributionRouteService>();
        builder.Services.AddScoped<LeadExportQuotaService>();
        builder.Services.AddScoped<DistributionEngine>();
        builder.Services.AddScoped<CandidateAutoDistributionService>();
        builder.Services.AddScoped<BitrixDuplicateCheckAllService>();
        builder.Services.AddScoped<CandidateBitrixSendService>();
        builder.Services.AddScoped<ResponseBitrixDeliveryService>();
        builder.Services.AddScoped<ResponseCacheInvalidator>();
        builder.Services.AddScoped<ManualBitrixSendService>();
        builder.Services.AddScoped<BulkResponsesBitrixSendService>();
        builder.Services.AddScoped<ResponseDeliveryService>();
        builder.Services.AddScoped<CandidateIngestionService>();
        builder.Services.AddScoped<BitrixLegacyMigrationService>();
        builder.Services.AddScoped<CandidateLookupService>();
        builder.Services.AddScoped<WorkerOutboundChatService>();
        builder.Services.AddScoped<WorkerMonitoringStatsService>();
        builder.Services.AddScoped<CandidatePersonMatchService>();
        builder.Services.AddScoped<CandidatePhoneWatchService>();
        builder.Services.AddScoped<CandidatePersonPhoneService>();
        builder.Services.AddScoped<CandidateDuplicateService>();
        builder.Services.AddScoped<OfficeBitrixWebhookResolver>();
        builder.Services.AddScoped<OfficeBitrixSettingsService>();
        builder.Services.AddScoped<OfficeBitrixIntegrationService>();
        builder.Services.AddScoped<ResponsesQueryService>();
        builder.Services.AddScoped<ResponseEditService>();
        builder.Services.AddSingleton<PhoneNormalizer>();
        builder.Services.AddSingleton<CandidateParser>();
        builder.Services.AddSingleton<BitrixClient>();
        builder.Services.AddSingleton<IBitrixWorkforceClient, BitrixWorkforceClient>();
        builder.Services.AddScoped<IBitrixCrmImportClient, BitrixCrmImportClient>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<OrbitaBitrixSettings>(builder.Configuration.GetSection("Bitrix"));
        builder.Services.Configure<BitrixWorkforceOptions>(
            builder.Configuration.GetSection(BitrixWorkforceOptions.SectionName));
        builder.Services.Configure<TelephonyGatewayPublicOptions>(
            builder.Configuration.GetSection(TelephonyGatewayPublicOptions.SectionName));
        builder.Services.AddScoped<PasswordPolicyService>();
        builder.Services.AddScoped<ServiceLogsQueryService>();
        builder.Services.AddHostedService<ServiceLogsCleanupService>();
        builder.Services.AddHostedService<WorkerScheduleHostedService>();
        builder.Services.AddHostedService<CrmDeadlineNotificationHostedService>();
        builder.Services.AddHostedService<CrmShiftSweeperService>();
        builder.Services.AddHostedService<CrmDailyDistributionHostedService>();
        builder.Services.AddHostedService<PlusofonRecordingHostedService>();
        builder.Services.AddHostedService<TelephonyProviderAccountSyncHostedService>();
        builder.Services.AddHostedService<ProviderRecordingArchiveHostedService>();
        builder.Services.AddHostedService<CrmCallAiProcessingHostedService>();
        builder.Services.AddHostedService<BitrixWorkforceHostedService>();
        builder.Services.AddHostedService<CrmTelephonyRuntimeSyncHostedService>();
        builder.Services.AddScoped<WebhookSecretProtector>();
        builder.Services.AddScoped<AvitoAccountSecretProtector>();
        builder.Services.AddScoped<BitrixWebhookValidator>();
        builder.Services.AddScoped<PanelBitrixIntegrationService>();
        builder.Services.Configure<LeadFlowImportOptions>(builder.Configuration.GetSection("LeadFlowImport"));
        builder.Services.AddSingleton<LeadFlowDatabaseReader>();
        builder.Services.AddScoped<LeadFlowImportService>();
        var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(dataProtectionPath))
        {
            Directory.CreateDirectory(dataProtectionPath);
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
                .SetApplicationName("Orbita.Api");
        }
        else
        {
            builder.Services.AddDataProtection()
                .SetApplicationName("Orbita.Api");
        }
        builder.Services.AddHttpClient(nameof(BitrixWebhookValidator), client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            .RemoveAllLoggers();
        builder.Services.AddHttpClient(nameof(BitrixClient), client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .RemoveAllLoggers();
        builder.Services.AddHttpClient(nameof(BitrixWorkforceClient), client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .RemoveAllLoggers();
        builder.Services.AddHttpClient(nameof(BitrixCrmImportClient), client =>
            {
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .RemoveAllLoggers();
        builder.Services.AddHttpClient(PlusofonApiClient.HttpClientName, client =>
            {
                client.BaseAddress = new Uri("https://restapi.plusofon.ru/");
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            .RemoveAllLoggers();
        builder.Services.AddHttpClient(ProviderRecordingArchiveService.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(2);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            .RemoveAllLoggers();
        builder.Services.AddHttpClient(CerioAiClient.HttpClientName, (services, client) =>
            {
                var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CerioAiOptions>>().Value;
                client.BaseAddress = Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var configured)
                    && configured.Scheme == Uri.UriSchemeHttps
                        ? configured
                        : new Uri("https://api.cerio.ru/");
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 30, 900));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            // The token is a query parameter, so disable request logging for this client.
            .RemoveAllLoggers();

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

    }
}
