using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Notifications;

public interface IEmailSender
{
    bool IsConfigured { get; }

    /// <summary>Sends one message. Returns false if delivery is off or failed; never throws.</summary>
    Task<bool> SendAsync(IReadOnlyCollection<string> to, string subject, string body, CancellationToken ct);
}

/// <summary>
/// SMTP delivery via MailKit (already a dependency for IMAP ingestion).
///
/// Deliberately never throws: a failed notification must not take down the worker
/// pass that produced it, and an unconfigured relay should degrade to logging
/// rather than erroring — a self-hosted install may legitimately not have SMTP.
/// </summary>
public sealed class EmailSender(IOptions<EmailOptions> options, ILogger<EmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<bool> SendAsync(
        IReadOnlyCollection<string> to, string subject, string body, CancellationToken ct)
    {
        var recipients = to.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (recipients.Count == 0)
        {
            return false;
        }

        if (!IsConfigured)
        {
            logger.LogInformation(
                "Email not configured (Email:Host / Email:FromAddress); would have sent \"{Subject}\" to {Count} recipient(s)",
                subject, recipients.Count);
            return false;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
            foreach (var address in recipients)
            {
                message.To.Add(MailboxAddress.Parse(address));
            }
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            await client.ConnectAsync(
                _options.Host,
                _options.Port,
                _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto,
                ct);

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                await client.AuthenticateAsync(_options.Username, _options.Password, ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            logger.LogInformation("Sent \"{Subject}\" to {Count} recipient(s)", subject, recipients.Count);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send \"{Subject}\" to {Count} recipient(s)", subject, recipients.Count);
            return false;
        }
    }
}
