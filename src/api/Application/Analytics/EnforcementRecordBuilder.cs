namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>
/// Turns the recommended enforcement step into the exact record to publish, by
/// editing the live one rather than composing a new one.
///
/// <para>
/// Composing from scratch is the tempting shortcut and the dangerous one: a
/// generated <c>v=DMARC1; p=quarantine</c> drops the <c>rua=</c> the operator is
/// relying on, and the reports stop arriving with nothing to say why. So the live
/// record is the starting point and only the policy tags move — <c>p</c>, <c>t</c>,
/// and <c>pct</c> where one is still published. <c>rua</c>, <c>ruf</c>, <c>sp</c>,
/// <c>adkim</c>, <c>aspf</c>, <c>fo</c> and any tag this code has never heard of
/// keep their published value and their position.
/// </para>
///
/// <para>
/// The ramp is RFC 9989's, not RFC 7489's. <c>pct=</c> is retired (RFC 9989
/// Appendix A.6) and is never emitted; the testing step is <c>t=y</c>, under which
/// a receiver "not apply the policy, but instead apply any special handling rules
/// it might have in place" — in practice one level below the stated policy
/// (RFC 9989 §4.7). So <c>p=quarantine; t=y</c> is the step between <c>p=none</c>
/// and enforced quarantine, and dropping <c>t=y</c> is a step of its own.
/// </para>
/// </summary>
public static class EnforcementRecordBuilder
{
    /// <summary>
    /// The record to publish, or null when there is nothing to hand over: no live record
    /// to edit, one this code cannot round-trip, or a step that moves neither <c>p</c>
    /// nor <c>t</c> — a retired <c>pct=</c> is dropped from a record a real step produces,
    /// but is not on its own a reason to produce one.
    /// </summary>
    /// <param name="domainName">The domain itself; the host is derived as <c>_dmarc.{domain}</c>.</param>
    /// <param name="liveRecord">
    /// The record as DNS returned it. Null or blank — a failed lookup, or a domain with no
    /// record of its own — yields null: a guessed record is worse than no record.
    /// </param>
    /// <param name="policy">The <c>p=</c> value of the recommended step.</param>
    /// <param name="testing">Whether the recommended step is in testing mode (<c>t=y</c>).</param>
    public static EnforcementRecordChangeDto? Build(
        string domainName, string? liveRecord, string policy, bool testing)
    {
        if (string.IsNullOrWhiteSpace(liveRecord) || string.IsNullOrWhiteSpace(policy))
        {
            return null;
        }

        var current = liveRecord.Trim();
        if (!TrySplitTags(current, out var tags))
        {
            // A record this code cannot round-trip — a segment that is not name=value, a
            // tag name published in a spelling receivers do not recognize, or a duplicate
            // tag name that invalidates the whole list. Rewriting any of them means
            // deciding what the operator meant. Showing nothing is the honest answer; the
            // record-inspection card already reports the record as it stands.
            return null;
        }

        // "This tag MUST be the first tag in the list. The tag value is case sensitive,
        // and the only possible value is 'DMARC1'. If the tag is not the first in the
        // list, the tag is absent, or the value is not 'DMARC1', then the entire record
        // MUST be ignored." (RFC 9989 §4.7). A record receivers ignore is not one to
        // hand back with a policy bolted on — the policy would be ignored with it.
        if (tags[0] is not { Name: "v", Value: "DMARC1" })
        {
            return null;
        }

        var changed = new List<string>();
        var notes = new List<string>();

        var pIndex = tags.FindIndex(t => t.Name == "p");
        if (pIndex < 0)
        {
            // RFC 9989 downgraded p= from required to RECOMMENDED, so a record without one
            // is legal — and §4.10.1 says exactly what receivers do with it. With "at least
            // one syntactically valid reporting URI" in rua=, a receiver "MUST act as if a
            // record containing 'p=none' was retrieved"; without one it "applies no DMARC
            // processing to this message". So writing p=none into a record that already has
            // a working rua= publishes what receivers already apply — no step at all, and
            // not something to hand over under a recommendation to change nothing. Without
            // that rua=, the same tag turns DMARC on, which is a step worth taking.
            if (policy != "none" || !HasReportingUri(tags))
            {
                // After the v= tag is where a reader expects it.
                tags.Insert(1, ("p", policy));
                changed.Add("p");
                pIndex = 1;
            }
        }
        else if (tags[pIndex].Value != policy)
        {
            // Values are compared as published, because receivers do: "Values MUST be
            // processed as case sensitive unless the specific tag description of
            // semantics specifies case insensitivity" (RFC 6376 §3.2, the tag-value
            // syntax RFC 9989 §4.7 adopts), and no DMARC tag description does. So
            // p=Reject is not p=reject to a receiver, and correcting it is a real step.
            tags[pIndex] = (tags[pIndex].Name, policy);
            changed.Add("p");
        }

        var tIndex = tags.FindIndex(t => t.Name == "t");
        if (testing)
        {
            if (tIndex < 0)
            {
                // Beside the policy it qualifies, or straight after v= when the record
                // leaves p= to the receiver's default.
                tags.Insert(pIndex >= 0 ? pIndex + 1 : 1, ("t", "y"));
                changed.Add("t");
            }
            else if (tags[tIndex].Value != "y")
            {
                tags[tIndex] = (tags[tIndex].Name, "y");
                changed.Add("t");
            }
        }
        else if (tIndex >= 0 && tags[tIndex].Value == "y")
        {
            // n is the default, so removing the tag says the same thing with less to read.
            tags.RemoveAt(tIndex);
            changed.Add("t");
        }

        // Nothing to publish unless the step itself moves a tag. A retired pct= is worth
        // removing, but it is not a *step*, and offering a record on the strength of it
        // alone contradicts the recommendation printed directly above: "keep it in place
        // and change nothing", with a record to publish underneath. Record inspection is
        // where a stale pct= is reported.
        if (changed.Count == 0)
        {
            return null;
        }

        var pctIndex = tags.FindIndex(t => t.Name == "pct");
        if (pctIndex >= 0)
        {
            var published = tags[pctIndex].Value;
            tags.RemoveAt(pctIndex);
            changed.Add("pct");
            notes.Add(
                $"pct={published} is dropped — RFC 9989 retired the tag, and a receiver that still " +
                "honours it would apply the policy to only part of your mail.");
        }

        if (testing)
        {
            notes.Add(
                $"t=y is the testing step: receivers see p={policy} but apply one level below it, so " +
                "nothing starts being blocked yet. Reports keep arriving, and the aligned share should " +
                "hold before you remove it.");
        }

        var spIndex = tags.FindIndex(t => t.Name == "sp");
        if (spIndex >= 0
            && Strength(tags[spIndex].Value) is { } spStrength
            && Strength(policy) is { } pStrength
            && spStrength < pStrength)
        {
            var sp = tags[spIndex];
            // sp is preserved rather than moved with p, which is what the issue asks for and
            // what "change only what the step changes" means. It does leave subdomains behind,
            // and that is worth saying out loud rather than discovering from a later report.
            //
            // When the step is a testing one the gap is real but not yet in effect: t= governs
            // "the Domain Owner Assessment Policy declared in the p, sp, and/or np tags"
            // (RFC 9989 §4.7), so under t=y both are applied one level below and the
            // organisational domain is not enforcing the new policy either. Saying subdomains
            // are being left behind *now* would describe a gap the operator cannot observe.
            notes.Add(testing
                ? $"sp={sp.Value} is kept as published. Under t=y receivers apply one level below " +
                  $"p= and sp= alike, so nothing changes for subdomains yet — but once you remove " +
                  $"t=y, subdomains stay at {sp.Value} while the organisational domain enforces " +
                  $"{policy}. Raise or remove sp= to bring them along."
                : $"sp={sp.Value} is kept as published, so subdomains stay at {sp.Value} while the " +
                  $"organisational domain moves to {policy}. Raise or remove sp= to bring them along.");
        }

        return new EnforcementRecordChangeDto(
            $"_dmarc.{domainName.Trim().Trim('.')}",
            current,
            string.Join("; ", tags.Select(t => $"{t.Name}={t.Value}")),
            changed,
            notes);
    }

    /// <summary>
    /// The tag names RFC 9989 §9.3 registers, in the spelling it registers them in.
    /// Only the spelling counts: a receiver looking for <c>rua</c> does not find
    /// <c>RUA</c>, so the two are different tags and only one of them is read.
    /// </summary>
    private static readonly string[] RegisteredTags =
        ["adkim", "aspf", "fo", "np", "p", "pct", "psd", "rf", "ri", "rua", "ruf", "sp", "t", "v"];

    /// <summary>
    /// Splits a record into its tags, keeping each name and value exactly as published —
    /// unlike the parser's dictionary, which loses order and collapses duplicates. Order
    /// and spelling both survive into the output.
    ///
    /// <para>
    /// Fails, rather than guessing, on the three shapes this code cannot safely rewrite.
    /// All three come from RFC 6376 §3.2, the "tag-value" syntax RFC 9989 §4.7 adopts
    /// wholesale: a segment that is not <c>name=value</c>; a tag name that is a known
    /// DMARC tag in the wrong case ("Tags MUST be interpreted in a case-sensitive
    /// manner") — a record carrying <c>RUA=</c> is a record getting no reports, and
    /// handing back a repaired-looking copy of it would bury that; and a repeated tag
    /// name ("if a tag name does occur more than once, the entire tag-list is invalid"),
    /// where editing the first occurrence leaves the second to contradict it.
    /// </para>
    /// </summary>
    private static bool TrySplitTags(string record, out List<(string Name, string Value)> tags)
    {
        tags = [];

        foreach (var part in record.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }

            var name = part[..eq].Trim();
            if (tags.Any(t => t.Name == name))
            {
                return false;
            }

            // Unknown tags are fine and are carried through untouched — RFC 9989 §4.8
            // has receivers ignore them, so they cost nothing and may mean something to
            // the operator. A *mis-cased* known tag is the one to refuse: it reads like
            // a tag that works and is not one.
            if (!RegisteredTags.Contains(name, StringComparer.Ordinal)
                && RegisteredTags.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            tags.Add((name, part[(eq + 1)..].Trim()));
        }

        return tags.Count > 0;
    }

    /// <summary>
    /// Whether <c>rua=</c> carries "at least one syntactically valid reporting URI"
    /// (RFC 9989 §4.10.1) — the condition under which a receiver treats a record with
    /// no <c>p=</c> as <c>p=none</c> rather than skipping DMARC for the message. The
    /// scheme is matched case-insensitively because URI schemes are (RFC 3986 §3.1).
    /// </summary>
    private static bool HasReportingUri(List<(string Name, string Value)> tags)
    {
        var rua = tags.Find(t => t.Name == "rua").Value;
        return rua is not null
            && rua.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(uri => uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                    || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// none &lt; quarantine &lt; reject; null for anything unrecognized, which includes a
    /// value in the wrong case — <c>sp=None</c> is a syntax error a receiver discards in
    /// favour of the default (RFC 9989 §4.8), leaving subdomains on <c>p=</c>, so there
    /// is no subdomain gap to warn about.
    /// </summary>
    private static int? Strength(string? policy) => policy?.Trim() switch
    {
        "none" => 0,
        "quarantine" => 1,
        "reject" => 2,
        _ => null,
    };
}
