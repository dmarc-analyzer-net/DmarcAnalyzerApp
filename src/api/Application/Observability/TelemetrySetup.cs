using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Hosting;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DmarcAnalyzer.Api.Application.Observability;

public static class TelemetrySetup
{
    /// <summary>Meter Npgsql publishes: connection pool state, command duration.</summary>
    private const string NpgsqlMeter = "Npgsql";

    /// <summary>
    /// Wires OpenTelemetry when the environment asks for it, and does nothing at all when it
    /// does not.
    /// <para>
    /// Takes <see cref="IHostApplicationBuilder"/> rather than a web builder so the one
    /// implementation serves every APP_MODE — each host branch in Program.cs gets the same
    /// treatment from a single call, instead of growing its own copy that can drift.
    /// </para>
    /// <para>
    /// Takes the <see cref="AppMode"/> and not its name. A string parameter invited each
    /// branch to spell the mode by hand, and one of them derived it — <c>mode == All ? "all"
    /// : "api"</c> — which was correct only because every other mode had already returned.
    /// That is the same shape as the bug that had <c>/api/v1/system/status</c> reporting
    /// <c>api</c> from an <c>all</c> container. Passing the mode itself is exact and cannot
    /// stop being exact when a mode is added.
    /// </para>
    /// <para>
    /// Endpoint, protocol, headers and sampler are all left to the SDK, which reads the OTEL_*
    /// variables itself. Re-reading them here would be a second implementation of the spec, and
    /// a worse one. Resource attributes are the exception, and only for the two the process
    /// knows about itself and an operator would otherwise have to restate per deployment:
    /// <c>app.mode</c> and <c>service.version</c>. Everything else still comes from
    /// OTEL_RESOURCE_ATTRIBUTES.
    /// </para>
    /// </summary>
    public static TelemetrySettings AddTelemetry(this IHostApplicationBuilder builder, AppMode mode)
    {
        var settings = TelemetrySettings.Resolve(builder.Configuration, mode.ToName());

        if (!settings.Enabled)
        {
            return settings;
        }

        // service.version, so "did this start after the upgrade" is answerable from the
        // telemetry rather than from deploy timestamps. Display and not the bare version:
        // on an `edge` image the release number alone identifies the wrong build.
        var version = AppVersion.Current.Display;

        // Both builders get the same attributes. Logs use the local `resource` and
        // traces/metrics the configured one, so a difference here would leave the two halves
        // of a deployment disagreeing about which version they are.
        var resource = ResourceBuilder.CreateDefault()
            .AddService(settings.ServiceName, serviceVersion: version)
            // Which half of the deployment a span came from. Both modes share a service name
            // on purpose, so a trace that starts in the API and a worker's ingestion pass sit
            // under one service, and this attribute tells them apart.
            .AddAttributes([new KeyValuePair<string, object>("app.mode", settings.AppMode)]);

        var otel = builder.Services.AddOpenTelemetry().ConfigureResource(r => r
            .AddService(settings.ServiceName, serviceVersion: version)
            .AddAttributes([new KeyValuePair<string, object>("app.mode", settings.AppMode)]));

        if (settings.Traces != TelemetrySink.None)
        {
            otel.WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        // Probes fire every few seconds, forever, in every deployment shape.
                        // Tracing them buries the requests worth looking at. The public
                        // MTA-STS routes are the same class of noise: sender fleets and
                        // Caddy's ask endpoint, high-volume, anonymous, individually
                        // uninteresting. Metrics still count all of them.
                        o.Filter = context => !IsProbe(context.Request.Path)
                            && !IsMtaStsPublic(context.Request.Path);
                    })
                    .AddHttpClientInstrumentation()
                    // Npgsql emits its own activities, so command-level spans come from the
                    // driver rather than from EF. That is the level that matters here: the
                    // 7.7s /enforcement request logged ~1s of EF command time, because EF
                    // measures to first row and not through streaming.
                    .AddNpgsql();

                Export(settings.Traces, tracing);
            });
        }

        if (settings.Metrics != TelemetrySink.None)
        {
            otel.WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(NpgsqlMeter);

                Export(settings.Metrics, metrics);
            });
        }

        if (settings.Logs != TelemetrySink.None)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.SetResourceBuilder(resource);

                // Without these the exported record keeps the message template and drops the
                // values, so a log line arrives at the collector as "Failed to parse DMARC
                // attachment for report source {ReportSourceId}" with no id in it.
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.ParseStateValues = true;

                switch (settings.Logs)
                {
                    case TelemetrySink.Otlp:
                        logging.AddOtlpExporter();
                        break;
                    case TelemetrySink.Console:
                        logging.AddConsoleExporter();
                        break;
                }
            });
        }

        return settings;
    }

    /// <summary>
    /// Logs what was turned on. Telemetry that is silently off is the failure everyone hits
    /// once: a variable set on the wrong service, no data arriving, and nothing to say so.
    /// </summary>
    public static void LogTelemetryStatus(this ILogger logger, TelemetrySettings settings)
    {
        if (!settings.Enabled)
        {
            logger.LogInformation(
                "OpenTelemetry is off. Set OTEL_EXPORTER_OTLP_ENDPOINT to export, " +
                "or OTEL_TRACES_EXPORTER=console to print spans locally.");
            return;
        }

        logger.LogInformation(
            "OpenTelemetry enabled for {ServiceName} (app.mode={AppMode}): traces={Traces}, metrics={Metrics}, logs={Logs}",
            settings.ServiceName, settings.AppMode, settings.Traces, settings.Metrics, settings.Logs);
    }

    /// <summary>
    /// Paths that exist to be polled. /health/* is obvious; auth/setup is here because it is
    /// the readiness target in both the Compose healthcheck and the chart, chosen over
    /// /health/ready because a 200 from it proves migrations have been applied and
    /// CanConnectAsync does not. It is a boolean "does an admin exist" check, so excluding it
    /// loses close to nothing — but it does mean the console's first-load call is not traced.
    /// </summary>
    private static bool IsProbe(PathString path)
        => path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/v1/auth/setup", StringComparison.OrdinalIgnoreCase);

    /// <summary>The anonymous MTA-STS routes: policy serving and Caddy's on_demand_tls ask.</summary>
    private static bool IsMtaStsPublic(PathString path)
        => path.Equals("/.well-known/mta-sts.txt", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/mta-sts/ask", StringComparison.OrdinalIgnoreCase);

    private static void Export(TelemetrySink sink, TracerProviderBuilder builder)
    {
        switch (sink)
        {
            case TelemetrySink.Otlp:
                builder.AddOtlpExporter();
                break;
            case TelemetrySink.Console:
                builder.AddConsoleExporter();
                break;
        }
    }

    private static void Export(TelemetrySink sink, MeterProviderBuilder builder)
    {
        switch (sink)
        {
            case TelemetrySink.Otlp:
                builder.AddOtlpExporter();
                break;
            case TelemetrySink.Console:
                builder.AddConsoleExporter();
                break;
        }
    }
}
