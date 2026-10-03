using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>
/// The clients one address covers, and for each whether its digest goes in the
/// address's roll-up or in a mail of its own. Never contains <c>off</c>: a client that
/// is off is simply not covered.
/// </summary>
public sealed record RecipientCoverage(string Email, IReadOnlyDictionary<Guid, string> Modes)
{
    public IEnumerable<Guid> Clients(string mode) => Modes.Where(x => x.Value == mode).Select(x => x.Key);
}

/// <summary>
/// Turns recipient rows into who-gets-what. The one place that decides coverage, so the
/// digest and alerts cannot disagree about which clients an address hears about.
/// <list type="bullet">
/// <item>A recipient with a <c>ClientId</c> covers that client, as a mail of its own.</item>
/// <item>A recipient without one covers every client its per-client rows and its default
/// mode do not switch off.</item>
/// <item>Rows sharing an address (compared case-insensitively) merge; where they disagree
/// about a client, <c>separate</c> beats <c>rollup</c>, so nobody loses a mail they asked for.</item>
/// </list>
/// </summary>
public static class NotificationRouting
{
    /// <summary>
    /// Coverage per address for recipients of <paramref name="kind"/> (<c>alert</c> or
    /// <c>digest</c>), restricted to <paramref name="clientIds"/>. With
    /// <paramref name="onlyRecipientId"/>, that one row alone, active or not — what a preview shows.
    /// </summary>
    public static async Task<IReadOnlyList<RecipientCoverage>> LoadAsync(
        DmarcAnalyzerDbContext db, string kind, IReadOnlyCollection<Guid> clientIds, CancellationToken ct,
        Guid? onlyRecipientId = null)
    {
        var rows = onlyRecipientId is { } id
            ? db.NotificationRecipients.Where(r => r.Id == id)
            : db.NotificationRecipients.Where(r => r.IsActive && (r.Kind == kind || r.Kind == "both"));
        var recipients = await rows
            .AsNoTracking()
            .Select(r => new
            {
                r.ClientId,
                r.Email,
                r.DigestDefaultMode,
                Modes = r.ClientModes.Select(m => new { m.ClientId, m.DigestMode }).ToList(),
            })
            .ToListAsync(ct);

        var byEmail = new Dictionary<string, Dictionary<Guid, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in recipients)
        {
            var email = recipient.Email.Trim();
            if (email.Length == 0)
            {
                continue;
            }

            if (!byEmail.TryGetValue(email, out var modes))
            {
                modes = [];
                byEmail[email] = modes;
            }

            if (recipient.ClientId is { } single)
            {
                if (clientIds.Contains(single))
                {
                    Merge(modes, single, DigestModes.Separate);
                }
                continue;
            }

            var overrides = recipient.Modes.ToDictionary(m => m.ClientId, m => m.DigestMode);
            foreach (var clientId in clientIds)
            {
                var mode = overrides.GetValueOrDefault(clientId, recipient.DigestDefaultMode);
                if (mode is DigestModes.Rollup or DigestModes.Separate)
                {
                    Merge(modes, clientId, mode);
                }
            }
        }

        return byEmail
            .Where(x => x.Value.Count > 0)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new RecipientCoverage(x.Key.ToLowerInvariant(), x.Value))
            .ToList();
    }

    private static void Merge(Dictionary<Guid, string> modes, Guid clientId, string mode)
    {
        if (!modes.TryGetValue(clientId, out var existing) || existing == DigestModes.Rollup)
        {
            modes[clientId] = mode;
        }
    }
}
