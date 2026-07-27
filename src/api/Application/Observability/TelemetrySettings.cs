namespace DmarcAnalyzer.Api.Application.Observability;

/// <summary>Where one signal is sent, if anywhere.</summary>
public enum TelemetrySink
{
    None,
    Otlp,
    Console,
}

/// <summary>
/// The telemetry decision, resolved from the standard <c>OTEL_*</c> environment variables.
/// <para>
/// Deliberately the OpenTelemetry spec's own variable names rather than an
/// <c>Observability:*</c> section of our own. A self-hoster pointing this at a collector
/// already has these values in hand from every other service they run, and can paste them
/// into any OTel tool unchanged. Inventing our own names would mean translating.
/// </para>
/// <para>
/// Separated from the wiring so the decision is testable without building a host: what
/// matters most is that a deployment which sets none of these behaves exactly as it did
/// before, and that is an assertion about this type.
/// </para>
/// </summary>
public sealed record TelemetrySettings(
    string ServiceName,
    string AppMode,
    TelemetrySink Traces,
    TelemetrySink Metrics,
    TelemetrySink Logs)
{
    public const string DefaultServiceName = "dmarc-analyzer";

    /// <summary>True when at least one signal has somewhere to go.</summary>
    public bool Enabled => Traces != TelemetrySink.None
        || Metrics != TelemetrySink.None
        || Logs != TelemetrySink.None;

    /// <summary>
    /// Resolves what to export. The rule is that telemetry costs nothing until asked for:
    /// with no endpoint and no explicit exporter choice, every signal is None and the SDK is
    /// never registered at all. Setting <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is therefore the
    /// single switch that turns everything on, which is what the spec leads people to expect.
    /// </summary>
    public static TelemetrySettings Resolve(IConfiguration configuration, string appMode)
    {
        // The spec's kill switch, honoured before anything else so it cannot be argued with.
        if (IsTrue(configuration["OTEL_SDK_DISABLED"]))
        {
            return new TelemetrySettings(
                ResolveServiceName(configuration), appMode,
                TelemetrySink.None, TelemetrySink.None, TelemetrySink.None);
        }

        var hasEndpoint = !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var fallback = hasEndpoint ? TelemetrySink.Otlp : TelemetrySink.None;

        return new TelemetrySettings(
            ResolveServiceName(configuration),
            appMode,
            ResolveSink(configuration["OTEL_TRACES_EXPORTER"], fallback),
            ResolveSink(configuration["OTEL_METRICS_EXPORTER"], fallback),
            ResolveSink(configuration["OTEL_LOGS_EXPORTER"], fallback));
    }

    private static string ResolveServiceName(IConfiguration configuration)
    {
        var configured = configuration["OTEL_SERVICE_NAME"];
        return string.IsNullOrWhiteSpace(configured) ? DefaultServiceName : configured.Trim();
    }

    /// <summary>
    /// An unrecognised value falls back rather than throwing. A typo in a telemetry variable
    /// must never be the reason a mail-ingesting service refuses to boot.
    /// </summary>
    private static TelemetrySink ResolveSink(string? value, TelemetrySink fallback)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" => fallback,
            "none" => TelemetrySink.None,
            "console" => TelemetrySink.Console,
            "otlp" => TelemetrySink.Otlp,
            _ => fallback,
        };

    private static bool IsTrue(string? value)
        => string.Equals((value ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
