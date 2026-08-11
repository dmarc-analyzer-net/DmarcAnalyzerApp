using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Clients;

/// <summary>
/// The catch-all client every install is bootstrapped with. It exists because a client is a
/// hard prerequisite for the first two things an operator reaches for — a domain and a
/// report source both require one — so without it the console dead-ends on a required
/// select with nothing in it. The domain list flags domains still sitting under this slug,
/// so the catch-all does not quietly become where everything lives.
/// </summary>
public static class DefaultClient
{
    public const string Name = "Default";
    public const string Slug = "default";

    /// <summary>
    /// Creates the default client, unless this install already has one of its own. Called
    /// from every path that bootstraps the first account — local registration and OIDC
    /// just-in-time provisioning both — so the client cannot be missing by any route in.
    /// Idempotent; returns null when there was nothing to do.
    /// </summary>
    public static async Task<Client?> EnsureAsync(DmarcAnalyzerDbContext db, CancellationToken ct)
    {
        if (await db.Clients.AnyAsync(ct))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var client = new Client
        {
            Name = Name,
            Slug = Slug,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);

        return client;
    }

    /// <summary>
    /// Whether this install still holds nothing an operator put here — the test a
    /// configuration <c>restore</c> is gated on, since an import that never deletes cannot
    /// reproduce a state something was deleted from.
    /// </summary>
    /// <remarks>
    /// The default client does not count against it. It is created during bootstrap, so
    /// requiring a literally empty <c>client</c> table would mean no install could ever be
    /// restored into — the recovery path would be dead on arrival.
    /// <para>
    /// Report sources do count, and have to: one can only be created against a client, and
    /// on a fresh install that client is the default one. Were they left out, an install
    /// with a configured report source and no domains yet would still read as pristine and
    /// let a restore union two installs together.
    /// </para>
    /// <para>
    /// Users are deliberately excluded, as before: the console's own bootstrap flow is how
    /// the operator got an account to run the restore with, so one always exists.
    /// </para>
    /// </remarks>
    public static async Task<bool> IsPristineInstallAsync(DmarcAnalyzerDbContext db, CancellationToken ct)
        => !await db.Clients.AnyAsync(x => x.Slug != Slug, ct)
            && !await db.Domains.AnyAsync(ct)
            && !await db.ReportSources.AnyAsync(ct);
}
