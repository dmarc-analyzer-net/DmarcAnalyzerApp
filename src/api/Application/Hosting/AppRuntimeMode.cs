namespace DmarcAnalyzer.Api.Application.Hosting;

/// <summary>What a container instance is for. Selected by the <c>APP_MODE</c> variable.</summary>
public enum AppMode
{
    /// <summary>HTTP API and the console UI. No background loop.</summary>
    Api,

    /// <summary>The background loop only — no Kestrel, no console.</summary>
    Worker,

    /// <summary>
    /// Both in one process. The intended shape for a single-host self-hoster:
    /// one container, one log stream, no healthcheck gate between services.
    /// </summary>
    All,
}

public static class AppRuntimeMode
{
    public const string EnvironmentVariable = "APP_MODE";

    /// <summary>Mode names accepted for <c>APP_MODE</c>, in the order they are documented.</summary>
    public static readonly string[] Names = ["api", "worker", "all"];

    /// <summary>
    /// Parses <c>APP_MODE</c>. Unset or blank means <see cref="AppMode.Api"/> —
    /// the Dockerfile's default and the value most deployments never touch.
    /// </summary>
    /// <remarks>
    /// Anything else throws rather than falling back. A silent fallback turns
    /// <c>APP_MODE=woker</c> into a machine that serves traffic and ingests
    /// nothing, which looks healthy from every angle an operator would check:
    /// the container is up, the UI loads, the healthcheck passes. The only
    /// symptom is reports quietly not arriving. Failing at startup costs one
    /// obvious crash instead.
    /// </remarks>
    public static AppMode Parse(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();

        return normalized switch
        {
            null or "" => AppMode.Api,
            "api" => AppMode.Api,
            "worker" => AppMode.Worker,
            "all" => AppMode.All,
            _ => throw new InvalidOperationException(
                $"{EnvironmentVariable}='{value}' is not a valid runtime mode. " +
                $"Expected one of: {string.Join(", ", Names)}."),
        };
    }

    /// <summary>Reads and parses the ambient <c>APP_MODE</c>.</summary>
    public static AppMode FromEnvironment()
        => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>Whether this mode runs the background loop in-process.</summary>
    public static bool RunsWorker(this AppMode mode) => mode is AppMode.Worker or AppMode.All;

    /// <summary>Whether this mode serves HTTP.</summary>
    public static bool RunsHttp(this AppMode mode) => mode is AppMode.Api or AppMode.All;
}
