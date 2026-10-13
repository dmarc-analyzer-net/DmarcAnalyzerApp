using System.Text.Json;

namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>
/// Severities of a <see cref="DmarcReportParseMessage"/>. Three levels, because operators
/// need to tell "this is how the report was read" from "this changed what was stored".
/// </summary>
public static class DmarcReportParseSeverity
{
    /// <summary>
    /// How the report was read; nothing it said was changed. The namespace strip on an
    /// RFC 9990 report is the case: every conformant report in that format raises it, so
    /// anything stronger would flag the whole format as faulty.
    /// </summary>
    public const string Info = "info";

    /// <summary>
    /// A repair that changed meaning — a value replaced or removed, a record dropped, a
    /// truncated document completed — or a schema warning.
    /// </summary>
    public const string Warning = "warning";

    /// <summary>A schema validation error.</summary>
    public const string Error = "error";

    /// <summary>Whether a message counts towards <c>dmarc_report.ParseWarningCount</c>.</summary>
    public static bool IsAtLeastWarning(string severity) => severity is Warning or Error;
}

/// <summary>
/// One thing the parser has to say about a report. Structured, rather than a
/// <c>"warning: ..."</c> prefix a caller has to split off, because the stored list is
/// filtered by severity and shown by severity.
/// </summary>
public sealed record DmarcReportParseMessage(string Severity, string Text)
{
    public static DmarcReportParseMessage Info(string text) => new(DmarcReportParseSeverity.Info, text);

    public static DmarcReportParseMessage Warning(string text) => new(DmarcReportParseSeverity.Warning, text);

    public static DmarcReportParseMessage Error(string text) => new(DmarcReportParseSeverity.Error, text);

    /// <summary>The one-line form: what a log line or a failing test shows.</summary>
    public override string ToString() => $"{Severity}: {Text}";
}

/// <summary>Where a report's stored parse messages came from (<c>dmarc_report.ParseMessagesOrigin</c>).</summary>
public static class DmarcReportParseOrigins
{
    /// <summary>Written by the parser that stored the report.</summary>
    public const string Ingest = "ingest";

    /// <summary>Written later, by re-parsing the archived report mail with a newer parser.</summary>
    public const string Backfill = "backfill";
}

/// <summary>
/// The JSON shape of <c>dmarc_report.ParseMessages</c>:
/// <c>[{ "severity": "warning", "text": "..." }]</c>. One place, so the raw-SQL insert, the
/// backfill and every reader agree on it.
/// </summary>
public static class DmarcReportParseMessagesJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<DmarcReportParseMessage> messages)
        => JsonSerializer.Serialize(messages, Options);

    public static IReadOnlyList<DmarcReportParseMessage> Deserialize(string json)
        => JsonSerializer.Deserialize<List<DmarcReportParseMessage>>(json, Options) ?? [];
}
