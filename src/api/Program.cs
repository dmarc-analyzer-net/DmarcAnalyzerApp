using Carter;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.MailboxSources;
using DmarcAnalyzer.Api.Application.Reports;
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
    workerBuilder.Services.AddScoped<IDmarcReportParser, DmarcRuaReportParser>();
    workerBuilder.Services.AddScoped<IMailboxSyncService, MailboxSyncService>();
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
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<IDomainService, DomainService>();
builder.Services.AddScoped<IMailboxSourceService, MailboxSourceService>();
builder.Services.AddScoped<IDmarcReportParser, DmarcRuaReportParser>();
builder.Services.AddScoped<IMailboxSyncService, MailboxSyncService>();
builder.Services.AddScoped<IMailboxSyncRunQueryService, MailboxSyncRunQueryService>();
builder.Services.AddScoped<IMailboxHealthQueryService, MailboxHealthQueryService>();
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
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseCors("frontend-dev");
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<SessionAuthMiddleware>();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (DmarcAnalyzerDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "unavailable" }, statusCode: 503));

app.MapCarter();
app.MapFallbackToFile("index.html");

await app.RunAsync();
