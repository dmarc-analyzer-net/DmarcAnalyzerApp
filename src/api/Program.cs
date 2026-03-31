using Carter;
using DmarcAnalyzer.Api.Data;
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

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("frontend-dev", policy =>
        {
            policy.WithOrigins("http://localhost:5173")
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
    });
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseCors("frontend-dev");
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapCarter();
app.MapFallbackToFile("index.html");

await app.RunAsync();
