using Carter;
using DmarcAnalyzer.Api.Application.Notifications;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Modules;

/// <summary>
/// The instance brand for the console. Public, because the sign-in page shows it
/// before anyone has a session, and nothing in it is secret: it is what the
/// agency puts in front of its clients anyway.
/// </summary>
public sealed class BrandingModule : ICarterModule
{
    /// <inheritdoc />
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/branding", (IOptions<BrandingOptions> branding) =>
        {
            var options = branding.Value;
            return Results.Ok(new
            {
                name = options.ResolvedName,
                logoUrl = options.ResolvedLogoUrl,
                accentColor = options.ResolvedAccentColor,
                customized = options.IsCustomized,
                showPoweredBy = options.IsCustomized && !options.HidePoweredBy,
            });
        });
    }
}
