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
    /// The record to publish, or null when there is nothing to hand over: no live
    /// record to edit, one this code cannot round-trip, or a step that changes nothing.
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
            // A record with a segment that is not name=value is malformed, and rewriting
            // it means deciding what that segment meant. Showing nothing is the honest
            // answer; the record-inspection card already reports the record as it stands.
            return null;
        }

        // v=DMARC1 has to come first for the record to be a DMARC record at all
        // (RFC 9989 §4.4), so a record not starting with it is not ours to edit.
        if (tags.Count == 0 || !tags[0].Name.Equals("v", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var changed = new List<string>();
        var notes = new List<string>();

        var pIndex = tags.FindIndex(t => t.Name.Equals("p", StringComparison.OrdinalIgnoreCase));
        if (pIndex < 0)
        {
            // RFC 9989 downgraded p= from required to RECOMMENDED, so a record without one
            // is legal. Publishing it explicitly is the step either way, and after the v=
            // tag is where a reader expects it.
            tags.Insert(1, ("p", policy));
            changed.Add("p");
            pIndex = 1;
        }
        else if (!tags[pIndex].Value.Equals(policy, StringComparison.OrdinalIgnoreCase))
        {
            tags[pIndex] = (tags[pIndex].Name, policy);
            changed.Add("p");
        }

        var tIndex = tags.FindIndex(t => t.Name.Equals("t", StringComparison.OrdinalIgnoreCase));
        if (testing)
        {
            if (tIndex < 0)
            {
                tags.Insert(pIndex + 1, ("t", "y"));
                changed.Add("t");
            }
            else if (!tags[tIndex].Value.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                tags[tIndex] = (tags[tIndex].Name, "y");
                changed.Add("t");
            }
        }
        else if (tIndex >= 0 && tags[tIndex].Value.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            // n is the default, so removing the tag says the same thing with less to read.
            tags.RemoveAt(tIndex);
            changed.Add("t");
        }

        var pctIndex = tags.FindIndex(t => t.Name.Equals("pct", StringComparison.OrdinalIgnoreCase));
        if (pctIndex >= 0)
        {
            var published = tags[pctIndex].Value;
            tags.RemoveAt(pctIndex);
            changed.Add("pct");
            notes.Add(
                $"pct={published} is dropped — RFC 9989 retired the tag, and a receiver that still " +
                "honours it would apply the policy to only part of your mail.");
        }

        if (changed.Count == 0)
        {
            return null;
        }

        if (testing)
        {
            notes.Add(
                $"t=y is the testing step: receivers see p={policy} but apply one level below it, so " +
                "nothing starts being blocked yet. Reports keep arriving, and the aligned share should " +
                "hold before you remove it.");
        }

        var spIndex = tags.FindIndex(t => t.Name.Equals("sp", StringComparison.OrdinalIgnoreCase));
        if (spIndex >= 0
            && Strength(tags[spIndex].Value) is { } spStrength
            && Strength(policy) is { } pStrength
            && spStrength < pStrength)
        {
            var sp = tags[spIndex];
            // sp is preserved rather than moved with p, which is what the issue asks for and
            // what "change only what the step changes" means. It does leave subdomains behind,
            // and that is worth saying out loud rather than discovering from a later report.
            notes.Add(
                $"sp={sp.Value} is kept as published, so subdomains stay at {sp.Value} while the " +
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
    /// Splits a record into its tags, keeping each name and value exactly as published —
    /// unlike the parser's dictionary, which lowercases nothing but also loses order and
    /// collapses duplicates. Order and spelling both survive into the output.
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

            tags.Add((part[..eq].Trim(), part[(eq + 1)..].Trim()));
        }

        return tags.Count > 0;
    }

    /// <summary>none &lt; quarantine &lt; reject; null for anything unrecognized.</summary>
    private static int? Strength(string? policy) => policy?.Trim().ToLowerInvariant() switch
    {
        "none" => 0,
        "quarantine" => 1,
        "reject" => 2,
        _ => null,
    };
}
