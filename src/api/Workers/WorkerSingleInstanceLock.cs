using DmarcAnalyzer.Api.Data;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DmarcAnalyzer.Api.Workers;

/// <summary>
/// Refuses to start if another worker is already running against this database.
/// <para>
/// Two ingestion loops against one database is not a supported configuration, and
/// the failures are quiet ones: two IMAP sessions per mailbox, two
/// <c>mailbox_sync_run</c> rows inflating the health counts, duplicate alert
/// emails (the cooldown is a read-then-write with no unique constraint behind
/// it), a duplicate monthly digest sent before the unique index rejects the
/// second row, <c>DbUpdateConcurrencyException</c> from the retention purge
/// deleting a batch another worker already deleted, and a checkpoint that can
/// move backwards because the update is unconditional. Reports themselves survive
/// — every insert is <c>ON CONFLICT DO NOTHING</c> against a real unique index —
/// so nothing corrupts. It just does the work twice and tells the operator things
/// that are not true.
/// </para>
/// <para>
/// The Helm chart refuses <c>worker.replicas &gt; 1</c>, but that only covers
/// Kubernetes: <c>docker compose up --scale worker=2</c> is not prevented by
/// anything Compose can express, and neither is running a worker beside an
/// <c>APP_MODE=all</c> container. A lock in the database covers every way of
/// arriving at the same state.
/// </para>
/// </summary>
public sealed class WorkerSingleInstanceLock(
    IConfiguration configuration,
    IOptions<WorkerOptions> options,
    ILogger<WorkerSingleInstanceLock> logger) : IHostedService, IAsyncDisposable
{
    /// <summary>
    /// Arbitrary but fixed: any two processes using this key contend, and nothing
    /// else in the database will pick it by accident.
    /// </summary>
    private const long LockKey = 0x444D_4152_4357_4B52; // "DMARCWKR"

    private NpgsqlConnection? _connection;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnforceSingleInstance)
        {
            logger.LogWarning(
                "Worker:EnforceSingleInstance is off. Nothing prevents a second worker from " +
                "running against this database, which duplicates ingestion and can send " +
                "duplicate alert and digest email.");
            return;
        }

        var connectionString = ConnectionStringResolver.Resolve(configuration);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Nothing to lock against, and the loop is about to fail on the same
            // missing setting with a clearer message than this one would give.
            return;
        }

        // A dedicated connection, held open for the life of the process: advisory
        // locks are scoped to a session, so it has to be this connection rather
        // than one borrowed from the pool and returned.
        _connection = new NpgsqlConnection(connectionString);
        await _connection.OpenAsync(cancellationToken);

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key)";
        command.Parameters.AddWithValue("key", LockKey);

        var acquired = await command.ExecuteScalarAsync(cancellationToken) as bool? ?? false;

        if (!acquired)
        {
            await DisposeAsync();

            throw new InvalidOperationException(
                "Another worker already holds the ingestion lock on this database. Running two " +
                "ingestion loops duplicates every sync pass and can send duplicate alert and " +
                "digest email, so this process is stopping instead. Run exactly one container " +
                "with APP_MODE=worker or APP_MODE=all. " +
                "If a previous worker was killed abruptly, its lock is released when Postgres " +
                "notices the dead connection, which can take a couple of minutes.");
        }

        logger.LogInformation("Acquired the ingestion lock; this is the only worker on this database.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Closing the connection releases the lock. Postgres also releases it if this
    /// process dies without closing, once it notices the connection is gone.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
