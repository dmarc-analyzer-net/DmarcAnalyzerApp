using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>
/// Writes a several-clients recipient's digest routing: its default mode and the
/// per-client rows that differ from it. Shared by the recipients API and the
/// configuration import, so both store routing the same way.
/// </summary>
public static class RecipientRouting
{
    /// <summary>
    /// Validates <paramref name="defaultMode"/> and <paramref name="modes"/>, then applies
    /// them with <see cref="Replace"/>. Returns an error message, or null on success.
    /// </summary>
    public static async Task<string?> ApplyAsync(
        DmarcAnalyzerDbContext db,
        NotificationRecipient recipient,
        string? defaultMode,
        IReadOnlyList<(Guid ClientId, string? Mode)> modes,
        CancellationToken ct)
    {
        if (recipient.ClientId is not null)
        {
            return "per-client modes apply only to a recipient covering several clients";
        }

        var normalisedDefault = Normalise(defaultMode);
        if (!DigestModes.IsValid(normalisedDefault))
        {
            return "digestDefaultMode must be rollup, separate, or off";
        }

        if (modes.Any(m => !DigestModes.IsValid(Normalise(m.Mode))))
        {
            return "each client mode must be rollup, separate, or off";
        }

        if (modes.Select(m => m.ClientId).Distinct().Count() != modes.Count)
        {
            return "a client appears more than once";
        }

        var ids = modes.Select(m => m.ClientId).ToList();
        if (await db.Clients.CountAsync(c => ids.Contains(c.Id), ct) != ids.Count)
        {
            return "one or more clients do not exist";
        }

        Replace(db, recipient, normalisedDefault, modes.ToDictionary(m => m.ClientId, m => Normalise(m.Mode)));
        return null;
    }

    /// <summary>
    /// Sets the default and makes the recipient's per-client rows match
    /// <paramref name="modes"/>, keeping only those that differ from the default — so a
    /// later change of default moves every client that was merely following it. Inputs
    /// are assumed valid.
    /// <para>
    /// Diffed rather than cleared and re-added, because a delete and an insert of the same
    /// (recipient, client) pair in one save would race the unique index. New rows are
    /// added to the context explicitly: their key is set client-side, and EF takes an
    /// entity with a key that merely appears in a tracked collection for an existing row,
    /// so it would issue an UPDATE that matches nothing.
    /// </para>
    /// </summary>
    public static void Replace(
        DmarcAnalyzerDbContext db,
        NotificationRecipient recipient,
        string defaultMode,
        IReadOnlyDictionary<Guid, string> modes)
    {
        recipient.DigestDefaultMode = defaultMode;

        var wanted = modes
            .Where(m => m.Value != defaultMode)
            .ToDictionary(m => m.Key, m => m.Value);

        foreach (var row in recipient.ClientModes.ToList())
        {
            if (wanted.Remove(row.ClientId, out var mode))
            {
                row.DigestMode = mode;
            }
            else
            {
                recipient.ClientModes.Remove(row);
            }
        }

        foreach (var (clientId, mode) in wanted)
        {
            var row = new NotificationRecipientClient
            {
                RecipientId = recipient.Id,
                ClientId = clientId,
                DigestMode = mode,
            };
            recipient.ClientModes.Add(row);
            db.NotificationRecipientClients.Add(row);
        }
    }

    private static string Normalise(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant();
}
