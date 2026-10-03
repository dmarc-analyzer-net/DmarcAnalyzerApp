using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>
/// Turns digest summaries into mail: a subject, an HTML body and a plain-text body that
/// says the same thing.
/// <para>
/// The HTML is what mail clients can actually render — nested tables, inline styles, no
/// stylesheet, no web fonts, no script — and every value that came from data (client and
/// domain names, hostnames, alert titles) is HTML-encoded, because a hostname is whatever
/// the owner of the IP put in their PTR record. The text body never aligns columns with
/// spaces: most clients show plain text in a proportional font, which is how the old
/// digest's columns came out ragged.
/// </para>
/// </summary>
public sealed partial class DigestRenderer(IOptions<BrandingOptions> branding, IOptions<EmailOptions> email)
{
    /// <summary>Domains listed in a client digest before the rest are summarised as a count.</summary>
    public const int MaxDomainRows = 25;

    /// <summary>Failing sources listed per client in a roll-up.</summary>
    public const int RollupSources = 5;

    private const string Ink = "#101f1c";
    private const string Secondary = "#54685f";
    private const string Border = "#e3eae8";
    private const string Page = "#f5f8f7";
    private const string Card = "#ffffff";
    private const string Sunken = "#eef3f1";
    private const string Sans = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";
    // Progressive enhancement: clients that honour a media query (most phone apps) get
    // tighter padding and lose the Change columns; the rest render the desktop layout.
    private const string PhoneCss = "@media (max-width:480px){.da-card{padding:20px 16px 24px !important}.da-wide{display:none !important}.da-data td,.da-data th{padding-left:4px !important;padding-right:4px !important}}";
    private const string Mono = "ui-monospace,SFMono-Regular,Menlo,Consolas,'Liberation Mono',monospace";

    private enum Tone { Ok, Warn, Danger, Neutral }

    private readonly string _brand = string.IsNullOrWhiteSpace(branding.Value.Name) ? "DMARC Analyzer" : branding.Value.Name.Trim();
    private readonly string _accent = HexColor().IsMatch(branding.Value.AccentColor ?? string.Empty)
        ? branding.Value.AccentColor!
        : BrandingOptions.DefaultAccentColor;
    private readonly string? _logo = Uri.TryCreate(branding.Value.LogoUrl, UriKind.Absolute, out var logo) &&
                                     (logo.Scheme == Uri.UriSchemeHttps || logo.Scheme == Uri.UriSchemeHttp)
        ? logo.AbsoluteUri
        : null;
    private readonly string? _baseUrl = string.IsNullOrWhiteSpace(email.Value.BaseUrl) ? null : email.Value.BaseUrl.TrimEnd('/');

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    // ---- Single client -------------------------------------------------------------

    /// <summary>One client's digest.</summary>
    public DigestMail RenderClient(DigestSummary s)
    {
        var month = DigestFormat.Month(s.PeriodStartUtc);
        var (verdict, tone) = Verdict(s);
        var subject = $"[DMARC] {s.ClientName}, {month}: {Lower(verdict)}";

        var html = new StringBuilder();
        Title(html, $"DMARC summary for {s.ClientName}", DigestFormat.Range(s.PeriodStartUtc, s.PeriodEndUtc));
        Banner(html, verdict, VerdictDetail(s), tone);
        Kpis(html,
            ("Pass rate", DigestFormat.Percent(s.ComplianceRate), DigestFormat.Change(s.ComplianceRate, s.PreviousComplianceRate)),
            ("Messages", DigestFormat.Count(s.Messages), $"{DigestFormat.Count(s.FailingMessages)} failed"),
            ("Domains", s.Domains.ToString(), $"{s.DomainsEnforcing} enforcing"),
            ("Failing sources", s.FailingSources.ToString(), $"{s.Alerts.Count} {DigestFormat.Plural(s.Alerts.Count, "alert", "alerts")}"));
        FindingsHtml(html, s);
        DomainTableHtml(html, s);
        SourcesHtml(html, s.TopFailingSources);
        AlertsHtml(html, s.Alerts);
        Button(html, ClientUrl(s.ClientId), $"Open {s.ClientName}");

        var text = new StringBuilder();
        text.AppendLine($"DMARC summary for {s.ClientName}");
        text.AppendLine(DigestFormat.Range(s.PeriodStartUtc, s.PeriodEndUtc));
        text.AppendLine();
        text.AppendLine($"{verdict}. {VerdictDetail(s)}");
        text.AppendLine();
        text.AppendLine($"Pass rate: {DigestFormat.Percent(s.ComplianceRate)} ({DigestFormat.Change(s.ComplianceRate, s.PreviousComplianceRate)})");
        text.AppendLine($"Messages: {DigestFormat.Count(s.Messages)}, of which {DigestFormat.Count(s.FailingMessages)} failed");
        text.AppendLine($"Domains: {s.Domains}, {s.DomainsEnforcing} at quarantine or reject");
        text.AppendLine($"Failing sources: {s.FailingSources}");
        text.AppendLine($"Alerts raised: {s.Alerts.Count}");
        FindingsText(text, s);
        DomainTableText(text, s);
        SourcesText(text, s.TopFailingSources);
        AlertsText(text, s.Alerts);
        LinkText(text, ClientUrl(s.ClientId), $"Open {s.ClientName}");

        return new DigestMail(subject, Footer(text), Wrap(html, subject), [s.ClientId]);
    }

    // ---- Roll-up -------------------------------------------------------------------

    /// <summary>Several clients in one mail: an overview of all, detail only for those with findings.</summary>
    public DigestMail RenderRollup(DateTime periodStart, IReadOnlyList<DigestSummary> clients)
    {
        var month = DigestFormat.Month(periodStart);
        var periodEnd = periodStart.AddMonths(1);
        var needing = clients.Count(c => c.Problems > 0);
        var suggestions = clients.Sum(c => c.Suggestions);
        var messages = clients.Sum(c => c.Messages);
        var compliant = clients.Sum(c => c.CompliantMessages);
        var prevClients = clients.Where(c => c.PreviousComplianceRate is not null).ToList();
        double? previous = null;
        if (prevClients.Count > 0)
        {
            // Volume-weighted, like the current rate. The previous month's volume is not in
            // the summary, so this month's stands in — close enough for a headline trend.
            var weight = prevClients.Sum(c => (double)c.Messages);
            previous = weight > 0 ? prevClients.Sum(c => c.PreviousComplianceRate!.Value * c.Messages) / weight : null;
        }
        var rate = messages == 0 ? 0 : (double)compliant / messages;

        var verdict = needing > 0
            ? $"{needing} of {clients.Count} clients need attention"
            : $"All clear across {clients.Count} clients";
        var detail = suggestions > 0
            ? $"{suggestions} {DigestFormat.Plural(suggestions, "suggestion", "suggestions")} to tighten a policy."
            : needing > 0 ? "Details for each one follow the overview." : "Nothing needs attention this month.";
        var subject = $"[DMARC] {month}: {Lower(verdict)}";

        var ordered = clients
            .OrderByDescending(c => c.Problems)
            .ThenByDescending(c => c.Suggestions)
            .ThenBy(c => c.ClientName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var html = new StringBuilder();
        Title(html, $"DMARC summary for {clients.Count} clients", DigestFormat.Range(periodStart, periodEnd));
        Banner(html, verdict, detail, needing > 0 ? Tone.Warn : Tone.Ok);
        Kpis(html,
            ("Pass rate", DigestFormat.Percent(rate), DigestFormat.Change(rate, previous)),
            ("Messages", DigestFormat.Count(messages), $"{DigestFormat.Count(messages - compliant)} failed"),
            ("Clients", clients.Count.ToString(), $"{needing} need attention"),
            ("Domains", clients.Sum(c => c.Domains).ToString(), $"{clients.Sum(c => c.DomainsEnforcing)} enforcing"));

        Heading(html, "Overview");
        html.Append(TableOpen());
        html.Append(HeaderRow(("Client", false), ("Status", false), ("Messages", true), ("Pass rate", true), ("Change" + WideOnly, true)));
        foreach (var c in ordered)
        {
            var (label, tone) = Status(c);
            html.Append("<tr>")
                .Append(Cell(LinkOrText(ClientUrl(c.ClientId), c.ClientName), false, bold: true))
                .Append(Cell(Chip(label, tone), false))
                .Append(Cell(E(DigestFormat.Count(c.Messages)), true))
                .Append(Cell(E(c.Messages == 0 ? "–" : DigestFormat.Percent(c.ComplianceRate)), true))
                .Append(Cell(E(c.Messages == 0 ? "–" : DigestFormat.Change(c.ComplianceRate, c.PreviousComplianceRate)), true, muted: true, wideOnly: true))
                .Append("</tr>");
        }
        html.Append("</table>");

        var text = new StringBuilder();
        text.AppendLine($"DMARC summary for {clients.Count} clients");
        text.AppendLine(DigestFormat.Range(periodStart, periodEnd));
        text.AppendLine();
        text.AppendLine($"{verdict}. {detail}");
        text.AppendLine();
        text.AppendLine($"Pass rate: {DigestFormat.Percent(rate)} ({DigestFormat.Change(rate, previous)})");
        text.AppendLine($"Messages: {DigestFormat.Count(messages)}, of which {DigestFormat.Count(messages - compliant)} failed");
        text.AppendLine();
        text.AppendLine("OVERVIEW");
        foreach (var c in ordered)
        {
            var (label, _) = Status(c);
            text.AppendLine(c.Messages == 0
                ? $"- {c.ClientName}: {label}"
                : $"- {c.ClientName}: {label}. {DigestFormat.Percent(c.ComplianceRate)} of {DigestFormat.Count(c.Messages)} messages passed ({DigestFormat.Change(c.ComplianceRate, c.PreviousComplianceRate)})");
        }

        foreach (var c in ordered.Where(c => c.Findings.Count > 0))
        {
            html.Append($"<div style=\"margin:32px 0 0;padding-top:20px;border-top:2px solid {Border}\">");
            html.Append($"<div style=\"font-size:17px;font-weight:600;color:{Ink}\">{LinkOrText(ClientUrl(c.ClientId), c.ClientName)}</div>");
            html.Append($"<div style=\"font-size:13px;color:{Secondary};margin-top:2px\">{E($"{DigestFormat.Percent(c.ComplianceRate)} of {DigestFormat.Count(c.Messages)} messages passed, {c.FailingSources} failing {DigestFormat.Plural(c.FailingSources, "source", "sources")}")}</div>");
            html.Append("</div>");
            FindingsHtml(html, c);
            SourcesHtml(html, c.TopFailingSources.Take(RollupSources).ToList());

            text.AppendLine();
            text.AppendLine();
            text.AppendLine(c.ClientName.ToUpperInvariant());
            FindingsText(text, c);
            SourcesText(text, c.TopFailingSources.Take(RollupSources).ToList());
            LinkText(text, ClientUrl(c.ClientId), $"Open {c.ClientName}");
        }

        if (ordered.Any(c => c.Findings.Count == 0))
        {
            html.Append($"<p style=\"margin:24px 0 0;font-size:13px;color:{Secondary}\">Clients with nothing to flag appear in the overview only.</p>");
        }

        return new DigestMail(subject, Footer(text), Wrap(html, subject), clients.Select(c => c.ClientId).ToList());
    }

    // ---- Shared pieces -------------------------------------------------------------

    private static (string Verdict, Tone Tone) Verdict(DigestSummary s)
        => s.Problems > 0
            ? ($"{s.Problems} {DigestFormat.Plural(s.Problems, "thing needs", "things need")} attention", Tone.Warn)
            : s.Messages == 0
                ? ("No reports this month", Tone.Neutral)
                : ("All clear", Tone.Ok);

    private static string VerdictDetail(DigestSummary s)
    {
        if (s.Problems > 0)
        {
            return s.Suggestions > 0
                ? $"Details below, plus {s.Suggestions} {DigestFormat.Plural(s.Suggestions, "suggestion", "suggestions")}."
                : "Details below.";
        }

        if (s.Messages == 0)
        {
            return "No aggregate reports arrived for any of this client's domains.";
        }

        return s.Suggestions > 0
            ? $"Nothing needs attention. {s.Suggestions} {DigestFormat.Plural(s.Suggestions, "domain is", "domains are")} ready for a stricter policy."
            : "Nothing needs attention this month.";
    }

    private static (string Label, Tone Tone) Status(DigestSummary c)
        => c.Problems > 0 ? ($"{c.Problems} {DigestFormat.Plural(c.Problems, "issue", "issues")}", Tone.Warn)
            : c.Suggestions > 0 ? ($"{c.Suggestions} {DigestFormat.Plural(c.Suggestions, "suggestion", "suggestions")}", Tone.Neutral)
            : c.Messages == 0 ? ("No reports", Tone.Neutral)
            : ("All clear", Tone.Ok);

    /// <summary>
    /// Findings grouped by what they are rather than by domain: five domains below the
    /// threshold are one block with five lines and the advice said once, not five copies
    /// of the same paragraph.
    /// </summary>
    private static IEnumerable<IGrouping<(DigestFindingKind Kind, string Advice), DigestFinding>> ByKind(
        IEnumerable<DigestFinding> findings)
        => findings.GroupBy(f => (f.Kind, f.Advice)).OrderBy(g => g.Key.Kind);

    private static string KindTitle(DigestFindingKind kind) => kind switch
    {
        DigestFindingKind.LowCompliance => "Pass rate below threshold",
        DigestFindingKind.ComplianceDrop => "Pass rate dropped",
        DigestFindingKind.NoReports => "Reports stopped",
        _ => "Ready for a stricter policy",
    };

    private void FindingsHtml(StringBuilder html, DigestSummary s)
    {
        void Section(string heading, IEnumerable<DigestFinding> findings, Tone tone)
        {
            var groups = ByKind(findings).ToList();
            if (groups.Count == 0)
            {
                return;
            }

            Heading(html, heading);
            var (fg, bg) = Colors(tone);
            foreach (var g in groups)
            {
                html.Append($"<div style=\"margin:0 0 10px;padding:12px 14px;border-left:3px solid {fg};background:{bg};border-radius:4px\">");
                html.Append($"<div style=\"font-size:14px;font-weight:600;color:{Ink}\">{E(KindTitle(g.Key.Kind))}</div>");
                foreach (var f in g)
                {
                    html.Append($"<div style=\"margin-top:6px;font-size:14px;color:{Ink}\">")
                        .Append($"<span style=\"font-family:{Mono};font-size:13px;font-weight:600;word-break:break-all\">{LinkOrText(DomainUrl(f.DomainId), f.Domain)}</span>")
                        .Append($"<br>{E(f.Summary)}</div>");
                }
                html.Append($"<div style=\"margin-top:8px;font-size:13px;color:{Secondary}\">{E(g.Key.Advice)}</div>");
                html.Append("</div>");
            }
        }

        Section("Needs attention", s.Findings.Where(f => f.IsProblem), Tone.Warn);
        Section("Suggestions", s.Findings.Where(f => !f.IsProblem), Tone.Ok);
    }

    private static void FindingsText(StringBuilder text, DigestSummary s)
    {
        void Section(string heading, IEnumerable<DigestFinding> findings)
        {
            var groups = ByKind(findings).ToList();
            if (groups.Count == 0)
            {
                return;
            }

            text.AppendLine();
            text.AppendLine(heading.ToUpperInvariant());
            foreach (var g in groups)
            {
                text.AppendLine($"{KindTitle(g.Key.Kind)}:");
                foreach (var f in g)
                {
                    text.AppendLine($"- {f.Domain}: {f.Summary}");
                }
                text.AppendLine($"  {g.Key.Advice}");
            }
        }

        Section("Needs attention", s.Findings.Where(f => f.IsProblem));
        Section("Suggestions", s.Findings.Where(f => !f.IsProblem));
    }

    private void DomainTableHtml(StringBuilder html, DigestSummary s)
    {
        if (s.DomainLines.Count == 0)
        {
            return;
        }

        Heading(html, "Domains");
        html.Append(TableOpen());
        html.Append(HeaderRow(("Domain", false), ("Policy", false), ("Messages", true), ("Pass rate", true), ("Change" + WideOnly, true)));
        foreach (var d in s.DomainLines.Take(MaxDomainRows))
        {
            html.Append("<tr>")
                .Append(Cell($"<span style=\"font-family:{Mono};font-size:13px;word-break:break-all\">{LinkOrText(DomainUrl(d.DomainId), d.Name)}</span>", false))
                .Append(Cell(Chip(d.Policy == "missing" ? "no record" : d.Policy, PolicyTone(d.Policy), mono: d.Policy != "missing"), false))
                .Append(Cell(E(DigestFormat.Count(d.Messages)), true))
                .Append(Cell(E(d.Messages == 0 ? "–" : DigestFormat.Percent(d.ComplianceRate)), true, bold: d.HasProblem))
                .Append(Cell(E(d.Messages == 0 ? "no reports" : DigestFormat.Change(d.ComplianceRate, d.PreviousComplianceRate)), true, muted: true, wideOnly: true))
                .Append("</tr>");
        }
        html.Append("</table>");

        if (s.DomainLines.Count > MaxDomainRows)
        {
            var more = s.DomainLines.Count - MaxDomainRows;
            html.Append($"<p style=\"margin:8px 0 0;font-size:13px;color:{Secondary}\">and {more} more {DigestFormat.Plural(more, "domain", "domains")}, all without findings.</p>");
        }
    }

    private static void DomainTableText(StringBuilder text, DigestSummary s)
    {
        if (s.DomainLines.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine("DOMAINS");
        foreach (var d in s.DomainLines.Take(MaxDomainRows))
        {
            text.AppendLine(d.Messages == 0
                ? $"- {d.Name} ({DigestFormat.Policy(d.Policy)}): no reports"
                : $"- {d.Name} ({DigestFormat.Policy(d.Policy)}): {DigestFormat.Percent(d.ComplianceRate)} of {DigestFormat.Count(d.Messages)} messages passed ({DigestFormat.Change(d.ComplianceRate, d.PreviousComplianceRate)})");
        }

        if (s.DomainLines.Count > MaxDomainRows)
        {
            text.AppendLine($"- and {s.DomainLines.Count - MaxDomainRows} more, all without findings");
        }
    }

    private void SourcesHtml(StringBuilder html, IReadOnlyList<DigestSource> sources)
    {
        if (sources.Count == 0)
        {
            return;
        }

        Heading(html, "Top failing sources");
        html.Append(TableOpen());
        html.Append(HeaderRow(("Source", false), ("Domain", false), ("Failed", true)));
        foreach (var src in sources)
        {
            var who = src.Hostname is { Length: > 0 } host
                ? $"<div style=\"font-size:13px;color:{Ink};word-break:break-all\">{E(host)}</div><div style=\"font-family:{Mono};font-size:12px;color:{Secondary};word-break:break-all\">{E(src.Ip)}</div>"
                : $"<div style=\"font-family:{Mono};font-size:13px;color:{Ink};word-break:break-all\">{E(src.Ip)}</div>";
            if (src.IsNew)
            {
                who += $"<div style=\"margin-top:3px\">{Chip("new this month", Tone.Warn)}</div>";
            }

            html.Append("<tr>")
                .Append(Cell(who, false))
                .Append(Cell($"<span style=\"font-family:{Mono};font-size:12px;word-break:break-all\">{E(src.DomainSummary)}</span>", false, muted: true))
                .Append(Cell(E(DigestFormat.Count(src.FailingMessages)), true))
                .Append("</tr>");
        }
        html.Append("</table>");
        html.Append($"<p style=\"margin:8px 0 0;font-size:12px;color:{Secondary}\">Failing means neither DKIM nor SPF passed with alignment. Forwarding and mailing lists show up here too, so a failing source is not necessarily abuse.</p>");
    }

    private static void SourcesText(StringBuilder text, IReadOnlyList<DigestSource> sources)
    {
        if (sources.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine("TOP FAILING SOURCES");
        foreach (var src in sources)
        {
            var who = src.Hostname is { Length: > 0 } host ? $"{host} ({src.Ip})" : src.Ip;
            text.AppendLine($"- {who}: {DigestFormat.Count(src.FailingMessages)} failed for {src.DomainSummary}{(src.IsNew ? ", new this month" : "")}");
        }
        text.AppendLine("Failing means neither DKIM nor SPF passed with alignment. Forwarding and mailing lists show up here too.");
    }

    private static void AlertsHtml(StringBuilder html, IReadOnlyList<DigestAlert> alerts)
    {
        if (alerts.Count == 0)
        {
            return;
        }

        Heading(html, "Alerts raised");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse\">");
        foreach (var a in alerts.Take(10))
        {
            html.Append("<tr>")
                .Append($"<td style=\"padding:6px 8px 6px 0;font-size:12px;color:{Secondary};white-space:nowrap;vertical-align:top\">{E(DigestFormat.Date(a.DetectedAtUtc))}</td>")
                .Append($"<td style=\"padding:6px 0;font-size:14px;color:{Ink}\">{E(a.Title)}</td>")
                .Append("</tr>");
        }
        html.Append("</table>");
        if (alerts.Count > 10)
        {
            html.Append($"<p style=\"margin:6px 0 0;font-size:13px;color:{Secondary}\">and {alerts.Count - 10} more.</p>");
        }
    }

    private static void AlertsText(StringBuilder text, IReadOnlyList<DigestAlert> alerts)
    {
        if (alerts.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine("ALERTS RAISED");
        foreach (var a in alerts.Take(10))
        {
            text.AppendLine($"- {DigestFormat.Date(a.DetectedAtUtc)}: {a.Title}");
        }
        if (alerts.Count > 10)
        {
            text.AppendLine($"- and {alerts.Count - 10} more");
        }
    }

    private static void LinkText(StringBuilder text, string? url, string label)
    {
        if (url is not null)
        {
            text.AppendLine();
            text.AppendLine($"{label}: {url}");
        }
    }

    // ---- HTML primitives -----------------------------------------------------------

    private string Wrap(StringBuilder body, string title)
    {
        var header = _logo is not null
            ? $"<img src=\"{E(_logo)}\" alt=\"{E(_brand)}\" height=\"32\" style=\"display:block;height:32px;max-height:40px;width:auto;border:0\">"
            : $"<span style=\"font-size:15px;font-weight:600;color:{Ink}\">{E(_brand)}</span>";

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light"><meta name="supported-color-schemes" content="light"><title>{{E(title)}}</title>
            <style>{{PhoneCss}}</style></head>
            <body style="margin:0;padding:0;background:{{Page}};font-family:{{Sans}};color:{{Ink}};-webkit-text-size-adjust:100%">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{{Page}};border-collapse:collapse"><tr><td align="center" style="padding:24px 12px">
            <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="width:100%;max-width:600px;border-collapse:collapse">
            <tr><td style="padding:0 0 12px">{{header}}</td></tr>
            <tr><td class="da-card" style="background:{{Card}};border:1px solid {{Border}};border-top:4px solid {{_accent}};border-radius:6px;padding:28px 28px 32px">
            {{body}}
            </td></tr>
            <tr><td style="padding:16px 4px;font-size:12px;line-height:18px;color:{{Secondary}}">Sent by {{E(_brand)}}. Pass rate is the share of messages where DKIM or SPF passed with alignment, as reported by receiving mail servers.</td></tr>
            </table></td></tr></table></body></html>
            """;
    }

    private string Footer(StringBuilder text)
    {
        text.AppendLine();
        text.AppendLine("--");
        text.AppendLine($"Sent by {_brand}. Pass rate is the share of messages where DKIM or SPF passed with alignment, as reported by receiving mail servers.");
        return text.ToString();
    }

    private static void Title(StringBuilder html, string title, string subtitle)
        => html.Append($"<h1 style=\"margin:0;font-size:22px;line-height:28px;font-weight:600;color:{Ink}\">{E(title)}</h1>")
            .Append($"<div style=\"margin-top:4px;font-size:14px;color:{Secondary}\">{E(subtitle)}</div>");

    private static void Banner(StringBuilder html, string verdict, string detail, Tone tone)
    {
        var (fg, bg) = Colors(tone);
        html.Append($"<div style=\"margin:20px 0 0;padding:14px 16px;background:{bg};border-radius:6px\">")
            .Append($"<div style=\"font-size:17px;font-weight:600;color:{fg}\">{E(verdict)}</div>")
            .Append($"<div style=\"margin-top:2px;font-size:14px;color:{fg}\">{E(detail)}</div>")
            .Append("</div>");
    }

    /// <summary>
    /// Two rows of two rather than one row of four: four tiles side by side leave about
    /// 80px each on a phone, and a mail cannot count on a media query to rearrange them.
    /// </summary>
    private static void Kpis(StringBuilder html, params (string Label, string Value, string Sub)[] tiles)
    {
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin-top:16px;border-collapse:separate;border-spacing:0\">");
        for (var i = 0; i < tiles.Length; i += 2)
        {
            html.Append("<tr>");
            for (var j = i; j < i + 2; j++)
            {
                var pad = $"{(i > 0 ? "8px" : "0")} {(j == i ? "4px" : "0")} 0 {(j == i ? "0" : "4px")}";
                html.Append($"<td width=\"50%\" style=\"padding:{pad};vertical-align:top\">");
                if (j < tiles.Length)
                {
                    var (label, value, sub) = tiles[j];
                    html.Append($"<div style=\"background:{Sunken};border-radius:6px;padding:10px 12px\">")
                        .Append($"<div style=\"font-size:12px;color:{Secondary}\">{E(label)}</div>")
                        .Append($"<div style=\"font-size:20px;line-height:26px;font-weight:600;color:{Ink}\">{E(value)}</div>")
                        .Append($"<div style=\"font-size:12px;color:{Secondary}\">{E(sub)}</div>")
                        .Append("</div>");
                }
                html.Append("</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</table>");
    }

    private static void Heading(StringBuilder html, string text)
        => html.Append($"<h2 style=\"margin:28px 0 10px;font-size:15px;font-weight:600;color:{Ink}\">{E(text)}</h2>");

    private static string TableOpen()
        => "<table class=\"da-data\" role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse\">";

    /// <summary>Header cells; a label ending in <see cref="WideOnly"/> marks a column hidden on phones.</summary>
    private static string HeaderRow(params (string Label, bool Right)[] columns)
    {
        var row = new StringBuilder("<tr>");
        foreach (var (raw, right) in columns)
        {
            var wide = raw.EndsWith(WideOnly, StringComparison.Ordinal);
            var label = wide ? raw[..^WideOnly.Length] : raw;
            row.Append($"<th{(wide ? " class=\"da-wide\"" : "")} align=\"{(right ? "right" : "left")}\" style=\"padding:6px 8px;border-bottom:1px solid {Border};font-size:12px;font-weight:500;color:{Secondary};white-space:nowrap\">{E(label)}</th>");
        }
        return row.Append("</tr>").ToString();
    }

    /// <summary>Suffix for a <see cref="HeaderRow"/> label whose column a phone-width screen drops.</summary>
    private const string WideOnly = "|wide";

    private static string Cell(string inner, bool right, bool bold = false, bool muted = false, bool wideOnly = false)
        => $"<td{(wideOnly ? " class=\"da-wide\"" : "")} align=\"{(right ? "right" : "left")}\" style=\"padding:8px;border-bottom:1px solid {Border};font-size:14px;vertical-align:top;" +
           $"color:{(muted ? Secondary : Ink)};{(bold ? "font-weight:600;" : "")}{(right ? "white-space:nowrap;" : "")}\">{inner}</td>";

    private static string Chip(string label, Tone tone, bool mono = false)
    {
        var (fg, bg) = Colors(tone);
        return $"<span style=\"display:inline-block;padding:1px 8px;border-radius:10px;background:{bg};color:{fg};font-size:12px;font-weight:500;white-space:nowrap;{(mono ? $"font-family:{Mono};" : "")}\">{E(label)}</span>";
    }

    private void Button(StringBuilder html, string? url, string label)
    {
        if (url is null)
        {
            return;
        }

        html.Append($"<div style=\"margin-top:28px\"><a href=\"{E(url)}\" style=\"display:inline-block;padding:10px 18px;background:{_accent};color:#ffffff;border-radius:6px;font-size:14px;font-weight:600;text-decoration:none\">{E(label)}</a></div>");
    }

    private string LinkOrText(string? url, string text)
        => url is null ? E(text) : $"<a href=\"{E(url)}\" style=\"color:{_accent};text-decoration:none\">{E(text)}</a>";

    private static Tone PolicyTone(string policy) => policy switch
    {
        "reject" => Tone.Ok,
        "quarantine" => Tone.Neutral,
        "missing" => Tone.Danger,
        _ => Tone.Warn,
    };

    private static (string Fg, string Bg) Colors(Tone tone) => tone switch
    {
        Tone.Ok => ("#0b5d54", "#d7f4ec"),
        Tone.Warn => ("#8a4406", "#fdf0d5"),
        Tone.Danger => ("#a81f3d", "#fde5ea"),
        _ => (Secondary, Sunken),
    };

    private string? ClientUrl(Guid clientId) => _baseUrl is null ? null : $"{_baseUrl}/domains?client={clientId}";

    private string? DomainUrl(Guid domainId) => _baseUrl is null ? null : $"{_baseUrl}/domains/{domainId}";

    private static string Lower(string verdict) => char.ToLowerInvariant(verdict[0]) + verdict[1..];

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
