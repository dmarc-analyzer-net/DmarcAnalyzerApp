using Carter;
using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Notifications;
using DmarcAnalyzer.Api.Application.Retention;
using DmarcAnalyzer.Api.Application.MailboxSources;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Application.Users;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Middleware;
using DmarcAnalyzer.Api.Modules;
using DmarcAnalyzer.Api.Workers;
using Microsoft.EntityFrameworkCore;

var mode = Environment.GetEnvironmentVariable("APP_MODE")?.Trim().ToLowerInvariant() ?? "api";

if (mode == "worker")
{
    var workerBuilder = Host.CreateApplicationBuilder(args);
    var workerConnectionString = workerBuilder.Configuration.GetConnectionString("Default")
        ?? "Host=localhost;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=postgres";

    workerBuilder.Services.AddDbContext<DmarcAnalyzerDbContext>(options =>
        options.UseNpgsql(workerConnectionString));
    workerBuilder.Services.AddCredentialProtection(workerBuilder.Configuration);
    workerBuilder.Services.AddScoped<IDmarcReportParser, DmarcRuaReportParser>();
    workerBuilder.Services.AddScoped<IMailboxSyncService, MailboxSyncService>();
    workerBuilder.Services.AddHttpContextAccessor();
    workerBuilder.Services.AddScoped<ICurrentUserContext, SystemUserContext>();
    workerBuilder.Services.AddScoped<IAuditLog, AuditLog>();
    workerBuilder.Services.Configure<RetentionOptions>(workerBuilder.Configuration.GetSection("Retention"));
    workerBuilder.Services.AddScoped<IRetentionPurgeService, RetentionPurgeService>();
    workerBuilder.Services.Configure<EmailOptions>(workerBuilder.Configuration.GetSection("Email"));
    workerBuilder.Services.Configure<AlertOptions>(workerBuilder.Configuration.GetSection("Alerts"));
    workerBuilder.Services.AddScoped<IEmailSender, EmailSender>();
    workerBuilder.Services.Configure<DigestOptions>(workerBuilder.Configuration.GetSection("Digest"));
    workerBuilder.Services.AddScoped<IAlertEvaluationService, AlertEvaluationService>();
    workerBuilder.Services.AddScoped<IDigestService, DigestService>();
    // DnsTxtResolver caches lookups in IMemoryCache; the worker host has to provide
    // it too, not just the API host.
    workerBuilder.Services.AddMemoryCache();
    workerBuilder.Services.Configure<DnsOptions>(workerBuilder.Configuration.GetSection("Dns"));
    workerBuilder.Services.AddSingleton<IDnsTxtResolver, DnsTxtResolver>();
    workerBuilder.Services.AddScoped<IDnsPolicyCache, DnsPolicyCache>();
    workerBuilder.Services.Configure<WorkerOptions>(workerBuilder.Configuration.GetSection("Worker"));
    workerBuilder.Services.AddHostedService<QueueWorkerService>();

    var workerHost = workerBuilder.Build();
    await workerHost.RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=postgres";

builder.Services.AddCarter();
builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddDbContext<DmarcAnalyzerDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddCredentialProtection(builder.Configuration);
builder.Services.AddOidcAuthentication(builder.Configuration);
builder.Services.AddScoped<IOidcSignInService, OidcSignInService>();
builder.Services.AddScoped<CurrentUserContext>();
builder.Services.AddScoped<ICurrentUserContext>(sp => sp.GetRequiredService<CurrentUserContext>());
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUserAdminService, UserAdminService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<IDomainService, DomainService>();
builder.Services.AddScoped<IMailboxSourceService, MailboxSourceService>();
builder.Services.AddScoped<IDmarcReportParser, DmarcRuaReportParser>();
builder.Services.AddScoped<IMailboxSyncService, MailboxSyncService>();
builder.Services.AddScoped<IMailboxSyncRunQueryService, MailboxSyncRunQueryService>();
builder.Services.AddScoped<IMailboxHealthQueryService, MailboxHealthQueryService>();
builder.Services.AddScoped<IAnalyticsQueryService, AnalyticsQueryService>();
builder.Services.AddScoped<IRecordInspectionService, RecordInspectionService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditLog, AuditLog>();
builder.Services.AddScoped<AuditQueryService>();
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));
builder.Services.AddScoped<IRetentionPurgeService, RetentionPurgeService>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection("Alerts"));
builder.Services.AddScoped<IEmailSender, EmailSender>();
builder.Services.Configure<DigestOptions>(builder.Configuration.GetSection("Digest"));
builder.Services.AddScoped<IAlertEvaluationService, AlertEvaluationService>();
builder.Services.AddScoped<IDigestService, DigestService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IHostnameResolver, HostnameResolver>();
builder.Services.AddSingleton<IDnsTxtResolver, DnsTxtResolver>();
builder.Services.Configure<DnsOptions>(builder.Configuration.GetSection("Dns"));
builder.Services.AddScoped<IDnsPolicyCache, DnsPolicyCache>();
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("frontend-dev", policy =>
        {
            policy.WithOrigins("http://localhost:5173")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });
}

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();

    // A schema change shipped by a deploy is the most audit-worthy event there
    // is, and until now only the manual endpoint recorded one. Capture the
    // pending list first: after MigrateAsync there is nothing left to report,
    // and an empty list means this boot changed nothing and warrants no entry.
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
    await db.Database.MigrateAsync();

    if (pending.Length > 0)
    {
        var audit = migrationScope.ServiceProvider.GetRequiredService<IAuditLog>();
        await audit.RecordSystemAsync(
            AuditEvents.DatabaseMigrated,
            $"Applied {pending.Length} pending database migration{(pending.Length == 1 ? "" : "s")} on startup",
            details: string.Join(", ", pending));
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseCors("frontend-dev");
}

app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Configuration.GetValue<bool>("Auth:Oidc:Enabled"))
{
    app.UseAuthentication();
}

app.UseMiddleware<SessionAuthMiddleware>();
app.UseMiddleware<RoleAuthorizationMiddleware>();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (DmarcAnalyzerDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "unavailable" }, statusCode: 503));

app.MapCarter();
app.MapFallbackToFile("index.html");

await app.RunAsync();
