using Carter;
using DmarcAnalyzer.Api.Contracts.MailboxSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Modules;

public sealed class MailboxSourcesModule : ICarterModule
{
    private static readonly string[] SupportedProtocols = ["imap", "pop3"];

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mailbox-sources", async (DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            var items = await db.MailboxSources
                .AsNoTracking()
                .Include(x => x.DefaultClient)
                .OrderBy(x => x.Name)
                .Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.Protocol,
                    x.Host,
                    x.Port,
                    x.UseTls,
                    x.Username,
                    x.DefaultClientId,
                    DefaultClientName = x.DefaultClient != null ? x.DefaultClient.Name : null,
                    x.IsActive,
                    x.LastSuccessSyncAtUtc,
                    x.CreatedAtUtc,
                    x.UpdatedAtUtc,
                })
                .ToListAsync(ct);

            return Results.Ok(items);
        });

        app.MapPost("/api/v1/mailbox-sources", async (CreateMailboxSourceRequest request, DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            var protocol = request.Protocol.Trim().ToLowerInvariant();
            if (!SupportedProtocols.Contains(protocol))
            {
                return Results.BadRequest(new { error = "protocol must be imap or pop3" });
            }

            if (string.IsNullOrWhiteSpace(request.Name) ||
                string.IsNullOrWhiteSpace(request.Host) ||
                string.IsNullOrWhiteSpace(request.Username) ||
                string.IsNullOrWhiteSpace(request.Password) ||
                request.Port <= 0 ||
                request.DefaultClientId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "name, host, port, username, password, and defaultClientId are required" });
            }

            var clientExists = await db.Clients.AnyAsync(x => x.Id == request.DefaultClientId, ct);
            if (!clientExists)
            {
                return Results.BadRequest(new { error = "default client not found" });
            }

            var now = DateTime.UtcNow;
            var source = new MailboxSource
            {
                Name = request.Name.Trim(),
                Protocol = protocol,
                Host = request.Host.Trim().ToLowerInvariant(),
                Port = request.Port,
                UseTls = request.UseTls,
                Username = request.Username.Trim(),
                PasswordEncrypted = request.Password,
                DefaultClientId = request.DefaultClientId,
                IsActive = request.IsActive,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            db.MailboxSources.Add(source);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/mailbox-sources/{source.Id}", source);
        });

        app.MapPatch("/api/v1/mailbox-sources/{id:guid}", async (Guid id, UpdateMailboxSourceRequest request, DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            var source = await db.MailboxSources.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (source is null)
            {
                return Results.NotFound();
            }

            if (request.Protocol is not null)
            {
                var protocol = request.Protocol.Trim().ToLowerInvariant();
                if (!SupportedProtocols.Contains(protocol))
                {
                    return Results.BadRequest(new { error = "protocol must be imap or pop3" });
                }

                source.Protocol = protocol;
            }

            if (request.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest(new { error = "name cannot be empty" });
                }

                source.Name = request.Name.Trim();
            }

            if (request.Host is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Host))
                {
                    return Results.BadRequest(new { error = "host cannot be empty" });
                }

                source.Host = request.Host.Trim().ToLowerInvariant();
            }

            if (request.Port.HasValue)
            {
                if (request.Port.Value <= 0)
                {
                    return Results.BadRequest(new { error = "port must be greater than 0" });
                }

                source.Port = request.Port.Value;
            }

            if (request.Username is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Username))
                {
                    return Results.BadRequest(new { error = "username cannot be empty" });
                }

                source.Username = request.Username.Trim();
            }

            if (request.Password is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Password))
                {
                    return Results.BadRequest(new { error = "password cannot be empty" });
                }

                source.PasswordEncrypted = request.Password;
            }

            if (request.DefaultClientId.HasValue)
            {
                if (request.DefaultClientId.Value == Guid.Empty)
                {
                    return Results.BadRequest(new { error = "defaultClientId cannot be empty" });
                }

                var clientExists = await db.Clients.AnyAsync(x => x.Id == request.DefaultClientId.Value, ct);
                if (!clientExists)
                {
                    return Results.BadRequest(new { error = "default client not found" });
                }

                source.DefaultClientId = request.DefaultClientId.Value;
            }

            if (request.UseTls.HasValue)
            {
                source.UseTls = request.UseTls.Value;
            }

            if (request.IsActive.HasValue)
            {
                source.IsActive = request.IsActive.Value;
            }

            source.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                source.Id,
                source.Name,
                source.Protocol,
                source.Host,
                source.Port,
                source.UseTls,
                source.Username,
                source.DefaultClientId,
                source.IsActive,
                source.LastSuccessSyncAtUtc,
                source.CreatedAtUtc,
                source.UpdatedAtUtc,
            });
        });
    }
}
