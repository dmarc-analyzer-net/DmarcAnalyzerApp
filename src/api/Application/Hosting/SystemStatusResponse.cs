using DmarcAnalyzer.Api.Application.Common;

namespace DmarcAnalyzer.Api.Application.Hosting;

/// <summary>
/// What <c>/api/v1/system/status</c> answers: which build this is, and which mode it
/// is running in.
/// <para>
/// A type with a factory rather than an anonymous object inside the module, so the
/// shape can be asserted without standing up a host. The endpoint had reported
/// <c>mode: "api"</c> unconditionally for as long as it existed — including in the
/// <c>all</c> containers the chart and Render both deploy — and nothing failed,
/// because nothing could see the payload without a running application.
/// </para>
/// </summary>
/// <param name="Service">Fixed identifier for the API, not a configured service name.</param>
/// <param name="Mode">
/// The resolved <c>APP_MODE</c>, in the spelling that selects it.
/// </param>
/// <param name="Version">The release, e.g. <c>0.9.0</c>.</param>
/// <param name="Revision">
/// The commit, or null on a release build. Full SHA — callers abbreviate.
/// </param>
public sealed record SystemStatusResponse(
    string Service,
    string Mode,
    string Version,
    string? Revision,
    DateTime TimestampUtc)
{
    /// <summary>The fixed <c>service</c> value in the payload.</summary>
    public const string ServiceName = "dmarc-analyzer-api";

    /// <summary>Builds the payload from the resolved mode and build info.</summary>
    public static SystemStatusResponse For(AppMode mode, AppVersionInfo version, DateTime nowUtc)
        => new(ServiceName, mode.ToName(), version.Version, version.Revision, nowUtc);
}
