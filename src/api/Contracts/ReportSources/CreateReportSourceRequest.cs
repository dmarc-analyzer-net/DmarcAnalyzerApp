namespace DmarcAnalyzer.Api.Contracts.ReportSources;

/// <summary>Body of POST /api/v1/report-sources. Which fields are required depends on the protocol.</summary>
public sealed class CreateReportSourceRequest
{
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = "imap";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseTls { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public Guid DefaultClientId { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Delete report mail from this mailbox once it is older than the retention window.
    /// Defaults to false: a new source must not start deleting a customer's mail because
    /// somebody left a field out of the request.
    /// </summary>
    public bool DeleteAfterRetention { get; set; }

    /// <summary>
    /// Whether this source may ingest reports for domains another client owns. Defaults
    /// to true, which is how every source behaved before the switch existed.
    /// </summary>
    public bool AllowForeignDomains { get; set; } = true;

    /// <summary>Bucket name. Required on an <c>s3</c> source, refused on any other.</summary>
    public string? S3Bucket { get; set; }

    /// <summary>
    /// Key prefix to poll. Also what bounds the cost of a pass, which lists every key under
    /// it — worth setting on a bucket that holds more than reports.
    /// </summary>
    public string? S3Prefix { get; set; }

    /// <summary>AWS region. Ignored when <see cref="S3Endpoint"/> is set.</summary>
    public string? S3Region { get; set; }

    /// <summary>Custom endpoint for MinIO, R2, B2 and anything else S3-compatible.</summary>
    public string? S3Endpoint { get; set; }

    /// <summary>
    /// Address the bucket as a path segment rather than a subdomain. Defaults true, which is
    /// what the S3-compatible services need and what AWS tolerates.
    /// </summary>
    public bool S3ForcePathStyle { get; set; } = true;
}
