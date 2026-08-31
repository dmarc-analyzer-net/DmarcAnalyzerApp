namespace DmarcAnalyzer.Api.Contracts.ReportSources;

/// <summary>Body of PATCH /api/v1/report-sources/{id}; null fields stay unchanged, an omitted secret keeps the stored one.</summary>
public sealed class UpdateReportSourceRequest
{
    public string? Name { get; set; }
    public string? Protocol { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public bool? UseTls { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public Guid? DefaultClientId { get; set; }
    public bool? IsActive { get; set; }

    /// <summary>
    /// Delete report mail from this mailbox once it is older than the retention window.
    /// Irreversible and off by default — see <c>ReportSource.DeleteAfterRetention</c>.
    /// </summary>
    public bool? DeleteAfterRetention { get; set; }

    /// <summary>
    /// Whether this source may ingest reports for domains another client owns. Defaults
    /// to true, which is how every source behaved before the switch existed.
    /// </summary>
    public bool? AllowForeignDomains { get; set; }

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

    /// <summary>Address the bucket as a path segment rather than a subdomain.</summary>
    public bool? S3ForcePathStyle { get; set; }
}
