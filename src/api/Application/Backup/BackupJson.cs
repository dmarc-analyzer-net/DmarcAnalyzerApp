using System.Text.Json;
using System.Text.Json.Serialization;

namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// Serialization for backup artifacts, deliberately independent of the API's response
/// serializer.
/// <para>
/// Every HTTP response in this app relies on ASP.NET's default web JSON policy, which
/// nothing in the repo pins. That is tolerable for a response read by a console built
/// from the same commit; it is not tolerable for a file written by one version and read
/// by another. Registering options globally would change every existing response, so
/// these live here and are passed explicitly at both ends —
/// <c>BackupArtifactFormatTests</c> asserts the resulting property names so a rename
/// cannot silently break stored artifacts.
/// </para>
/// </summary>
public static class BackupJson
{
    /// <summary>
    /// Bumped only for a change an older reader could not handle. Adding an optional
    /// property is not such a change; removing or renaming one is.
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Indented on purpose. These files get read by people mid-incident, diffed between
    /// two days to see what an operator changed, and committed to private repositories —
    /// all of which beat the bytes saved by compacting. They also gzip well.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    /// <summary>
    /// Reading is case-insensitive so an artifact hand-edited into PascalCase still
    /// imports. Writing stays strictly camelCase.
    /// </summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(BackupArtifact artifact)
        => JsonSerializer.Serialize(artifact, Options);
}
