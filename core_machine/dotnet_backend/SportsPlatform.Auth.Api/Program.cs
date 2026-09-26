using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using SportsPlatform.Auth.Api.Hubs;
using SportsPlatform.Auth.Api.Middleware;
using SportsPlatform.Auth.Api.Services;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Services;
using SportsPlatform.Auth.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(
        builder.Environment.ContentRootPath,
        ".keys")))
    .SetApplicationName("SportsPlatform.Auth");

// ── Database ────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("DefaultConnection is not configured.");

var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);

// Map PostgreSQL enums so EF Core <-> Npgsql can serialize them
dataSourceBuilder.MapEnum<AuthProviderType>("auth_provider_type");
dataSourceBuilder.MapEnum<RoleNameType>("role_name_type");
dataSourceBuilder.MapEnum<InvitationStatus>("invitation_status");
dataSourceBuilder.MapEnum<MembershipStatus>("membership_status");
dataSourceBuilder.MapEnum<EventType>("event_type");
dataSourceBuilder.MapEnum<AttendanceStatus>("attendance_status");
dataSourceBuilder.MapEnum<AnnouncementPriority>("announcement_priority");
dataSourceBuilder.MapEnum<PlanVisibility>("plan_visibility");

var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(dataSource, npgsqlOptions =>
    {
        // Also register enum mappings on the EF provider options to avoid int fallback.
        npgsqlOptions.MapEnum<AuthProviderType>("auth_provider_type");
        npgsqlOptions.MapEnum<RoleNameType>("role_name_type");
        npgsqlOptions.MapEnum<InvitationStatus>("invitation_status");
        npgsqlOptions.MapEnum<MembershipStatus>("membership_status");
        npgsqlOptions.MapEnum<EventType>("event_type");
        npgsqlOptions.MapEnum<AttendanceStatus>("attendance_status");
        npgsqlOptions.MapEnum<AnnouncementPriority>("announcement_priority");
        npgsqlOptions.MapEnum<PlanVisibility>("plan_visibility");
    }));

// ── Authentication ──────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(jwtSecret)),
        ClockSkew = TimeSpan.Zero
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            // Allow a query-string token for SignalR hubs and for media that is
            // streamed by the native video player (which can't reliably send an
            // Authorization header), e.g. game video /stream endpoints.
            var isHub = path.StartsWithSegments("/hubs/notifications");
            var isVideoStream = path.HasValue
                && path.Value.EndsWith("/stream", StringComparison.OrdinalIgnoreCase)
                && path.Value.Contains("/videos/", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(accessToken) && (isHub || isVideoStream))
                context.Token = accessToken;
            return Task.CompletedTask;
        }
    };
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme) // Required for Google OAuth sign-in scheme
.AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
{
    var callbackPath = builder.Configuration["Google:CallbackPath"] ?? "/auth/google/callback";

    options.ClientId = builder.Configuration["Google:ClientId"] ?? "PLACEHOLDER_CLIENT_ID";
    options.ClientSecret = builder.Configuration["Google:ClientSecret"] ?? "PLACEHOLDER_CLIENT_SECRET";
    options.CallbackPath = callbackPath;
});

// ── Authorization ───────────────────────────────────────────
builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

// ── Services ────────────────────────────────────────────────
builder.Services.AddScoped<ITokenService, TokenService>();
var storageProvider = builder.Configuration["Storage:Provider"];
if (storageProvider == "S3")
{
builder.Services.AddScoped<IFileStorageService, GoogleCloudStorageService>();
}
else
{
    builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
}
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ITeamService, TeamService>();
builder.Services.AddScoped<IClubService, ClubService>();
builder.Services.AddScoped<IInvitationService, InvitationService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IPlayerService, PlayerService>();
builder.Services.AddScoped<IEventService, EventService>();
builder.Services.AddScoped<IAttendanceService, AttendanceService>();
builder.Services.AddScoped<IMedicalService, MedicalService>();
builder.Services.AddScoped<IFitnessService, FitnessService>();
builder.Services.AddScoped<IAnnouncementService, AnnouncementService>();
builder.Services.AddScoped<ICoachingPlanService, CoachingPlanService>();
builder.Services.AddScoped<IGameStatsService, GameStatsService>();
builder.Services.AddScoped<IMessagingService, MessagingService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddSingleton<Mp4StreamingOptimizer>();
builder.Services.AddSingleton<IRealtimeConnectionTracker, NotificationConnectionTracker>();
builder.Services.AddScoped<INotificationRealtimePublisher, SignalRNotificationPublisher>();
builder.Services.AddHostedService<NotificationMaintenanceService>();
builder.Services.AddHostedService<VideoMaintenanceService>();

// Basketball PDF extraction runs as a sidecar on the backend machine. The URL is
// configurable so containers use service DNS while local development keeps localhost.
builder.Services.AddHttpClient(GameStatsService.StatsExtractorHttpClientName, (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["StatsExtractor:BaseUrl"] ?? "http://localhost:8100";
    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(60);
});

// ── Chatbot / prediction microservice integration ───────────
// Named HttpClient carries the shared bearer service token + base URL so the
// dispatcher just POSTs relative paths. Sport-gated at the controller call site.
builder.Services.AddHttpClient(ChatbotWebhookDispatcher.HttpClientName, (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["Microservice:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl))
        client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
    var token = cfg["Microservice:ServiceToken"];
    if (!string.IsNullOrWhiteSpace(token))
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    // The microservice's /ask runs a chain of Groq LLM calls (follow-up rewrite →
    // classification → answer generation) that routinely exceeds 10s on a cold
    // first request, and the very first ask for a team also pays for the backfill
    // status check. 10s was too tight and surfaced as a 502 "servers are having a
    // moment" on the first message; 100s gives the LLM chain headroom.
    client.Timeout = TimeSpan.FromSeconds(100);
});
builder.Services.AddScoped<IChatbotWebhookDispatcher, ChatbotWebhookDispatcher>();

builder.Services.AddSignalR(options =>
{
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(35);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

// ── Controllers ─────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

var app = builder.Build();

// ── Startup: Apply migrations ──────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<SqlMigrationRunner>>();

    // Step 1: EF Core migrations (entity tables, columns, indexes)
    await db.Database.MigrateAsync();

    // Step 2: Numbered SQL scripts (RLS, functions, triggers, views)
    var runner = new SqlMigrationRunner(db, logger);
    var scriptsDir = Path.Combine(AppContext.BaseDirectory, "scripts", "migrations");
    await runner.RunPendingMigrationsAsync(scriptsDir);
}

// ── Middleware ───────────────────────────────────────────────
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Serve static files from wwwroot/ (console, assets, uploaded chat media, etc.).
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var extension = Path.GetExtension(context.File.Name);
        if (IsVideoExtension(extension))
        {
            context.Context.Response.Headers[HeaderNames.AcceptRanges] = "bytes";
            context.Context.Response.Headers[HeaderNames.CacheControl] = "private, max-age=3600";
        }
    }
});

// Serve the React SPA from wwwroot/dist/
var distPath = app.Environment.WebRootPath is null
    ? null
    : Path.Combine(app.Environment.WebRootPath, "dist");
if (distPath is not null && Directory.Exists(distPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distPath),
        RequestPath = ""
    });
}

app.UseRouting();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseMiddleware<RlsMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapHub<NotificationHub>("/hubs/notifications");

// ── Health check ────────────────────────────────────────────
app.MapGet("/api/health", () => Results.Ok(new
{
    service = "SportsPlatform.Auth",
    status = "running",
    timestamp = DateTime.UtcNow
}));

// ── SPA fallback ────────────────────────────────────────────
// For any request that doesn't match an API route or static file,
// serve the React app's index.html (client-side routing)
if (distPath is not null && File.Exists(Path.Combine(distPath, "index.html")))
{
    app.MapFallbackToFile("dist/index.html");
}

static bool IsVideoExtension(string? extension) =>
    extension?.ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv" or ".avi" or ".3gp";

app.Run();
