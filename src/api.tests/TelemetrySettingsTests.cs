using DmarcAnalyzer.Api.Application.Observability;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The contract for the OTEL_* variables. The property that matters most is the first one:
/// an existing deployment that sets none of these must be unchanged, because telemetry is
/// being added to a service that ingests mail and must not acquire a new way to fail.
/// </summary>
public sealed class TelemetrySettingsTests
{
    private static TelemetrySettings Resolve(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();

        return TelemetrySettings.Resolve(configuration, "api");
    }

    [Fact]
    public void WithNothingConfigured_EverythingIsOff()
    {
        var settings = Resolve();

        Assert.False(settings.Enabled);
        Assert.Equal(TelemetrySink.None, settings.Traces);
        Assert.Equal(TelemetrySink.None, settings.Metrics);
        Assert.Equal(TelemetrySink.None, settings.Logs);
    }

    /// <summary>Setting an endpoint is the one switch that turns all three signals on.</summary>
    [Fact]
    public void AnEndpointAloneEnablesAllThreeSignals()
    {
        var settings = Resolve(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"));

        Assert.True(settings.Enabled);
        Assert.Equal(TelemetrySink.Otlp, settings.Traces);
        Assert.Equal(TelemetrySink.Otlp, settings.Metrics);
        Assert.Equal(TelemetrySink.Otlp, settings.Logs);
    }

    /// <summary>Printing spans locally must not require standing up a collector.</summary>
    [Fact]
    public void ConsoleExporterWorksWithNoEndpoint()
    {
        var settings = Resolve(("OTEL_TRACES_EXPORTER", "console"));

        Assert.True(settings.Enabled);
        Assert.Equal(TelemetrySink.Console, settings.Traces);
        // Only what was asked for: an unset signal stays off rather than following along.
        Assert.Equal(TelemetrySink.None, settings.Metrics);
        Assert.Equal(TelemetrySink.None, settings.Logs);
    }

    [Fact]
    public void PerSignalChoicesOverrideTheEndpointDefault()
    {
        var settings = Resolve(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("OTEL_METRICS_EXPORTER", "none"),
            ("OTEL_LOGS_EXPORTER", "console"));

        Assert.Equal(TelemetrySink.Otlp, settings.Traces);
        Assert.Equal(TelemetrySink.None, settings.Metrics);
        Assert.Equal(TelemetrySink.Console, settings.Logs);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void SdkDisabledBeatsEverythingElse(string value)
    {
        var settings = Resolve(
            ("OTEL_SDK_DISABLED", value),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("OTEL_TRACES_EXPORTER", "console"));

        Assert.False(settings.Enabled);
    }

    /// <summary>
    /// A typo in a telemetry variable must not stop a mail-ingesting service from booting, so
    /// an unrecognised value falls back to whatever the endpoint implies instead of throwing.
    /// </summary>
    [Theory]
    [InlineData("otpl", TelemetrySink.Otlp)]      // transposed
    [InlineData("jaeger", TelemetrySink.Otlp)]    // real exporter, not one we ship
    [InlineData("  OTLP  ", TelemetrySink.Otlp)]  // padded and shouted
    public void AnUnrecognisedExporterFallsBackInsteadOfThrowing(string value, TelemetrySink expected)
    {
        var settings = Resolve(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("OTEL_TRACES_EXPORTER", value));

        Assert.Equal(expected, settings.Traces);
    }

    [Fact]
    public void AnUnrecognisedExporterWithNoEndpointStaysOff()
    {
        var settings = Resolve(("OTEL_TRACES_EXPORTER", "nonsense"));

        Assert.False(settings.Enabled);
    }

    [Fact]
    public void ServiceNameDefaultsAndIsOverridable()
    {
        Assert.Equal(TelemetrySettings.DefaultServiceName, Resolve().ServiceName);
        Assert.Equal("dmarc-prod", Resolve(("OTEL_SERVICE_NAME", " dmarc-prod ")).ServiceName);
    }

    [Fact]
    public void AppModeIsCarriedThroughForTheResourceAttribute()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal("worker", TelemetrySettings.Resolve(configuration, "worker").AppMode);
    }
}
