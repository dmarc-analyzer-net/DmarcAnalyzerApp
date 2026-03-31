using Carter;
using DmarcAnalyzer.Api.Modules;
using DmarcAnalyzer.Api.Workers;

var mode = Environment.GetEnvironmentVariable("APP_MODE")?.Trim().ToLowerInvariant() ?? "api";

if (mode == "worker")
{
    var workerBuilder = Host.CreateApplicationBuilder(args);
    workerBuilder.Services.AddHostedService<QueueWorkerService>();

    var workerHost = workerBuilder.Build();
    await workerHost.RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCarter();
builder.Services.AddRouting(options => options.LowercaseUrls = true);

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
