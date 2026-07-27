using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// S3 and anything that speaks its API — MinIO, Cloudflare R2, Backblaze B2.
/// <para>
/// Registered as a singleton because <c>AmazonS3Client</c> is thread-safe and holds a
/// connection pool; constructing one per pass would leak sockets.
/// </para>
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly BackupOptions _options;
    private readonly ILogger<S3ObjectStorage> _logger;
    private readonly AmazonS3Client? _client;

    public S3ObjectStorage(IOptions<BackupOptions> options, ILogger<S3ObjectStorage> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.Bucket))
        {
            // Not an error: no bucket is the default, and it means backup offload is off.
            return;
        }

        var config = new AmazonS3Config
        {
            ForcePathStyle = _options.ForcePathStyle,
        };

        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            // ServiceURL and RegionEndpoint are mutually exclusive in the SDK; setting
            // both throws, so an explicit endpoint wins and the region is only carried
            // as the signing region.
            config.ServiceURL = _options.Endpoint;
            config.AuthenticationRegion = string.IsNullOrWhiteSpace(_options.Region)
                ? "us-east-1"
                : _options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(
                string.IsNullOrWhiteSpace(_options.Region) ? "us-east-1" : _options.Region);
        }

        // Empty credentials fall through to the ambient chain — an instance role or IRSA,
        // which is preferable to a long-lived key sitting in configuration.
        _client = string.IsNullOrWhiteSpace(_options.AccessKeyId)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(
                new BasicAWSCredentials(_options.AccessKeyId, _options.SecretAccessKey), config);
    }

    public bool IsConfigured => _client is not null;

    public string Describe()
        => string.IsNullOrWhiteSpace(_options.Endpoint)
            ? $"s3://{_options.Bucket} ({_options.Region})"
            : $"{_options.Endpoint.TrimEnd('/')}/{_options.Bucket}";

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        var client = Require();

        using var stream = new MemoryStream(content, writable: false);

        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            // Set explicitly: the SDK would otherwise compute a chunked signature, which
            // several S3-compatible backends reject.
            DisablePayloadSigning = false,
        }, ct);
    }

    public async Task<long?> GetLengthAsync(string key, CancellationToken ct)
    {
        var client = Require();

        try
        {
            var metadata = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _options.Bucket,
                Key = key,
            }, ct);

            return metadata.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        var client = Require();

        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _options.Bucket,
                Key = key,
            }, ct);

            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);

            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task CopyAsync(string sourceKey, string destinationKey, CancellationToken ct)
    {
        var client = Require();

        await client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _options.Bucket,
            SourceKey = sourceKey,
            DestinationBucket = _options.Bucket,
            DestinationKey = destinationKey,
        }, ct);
    }

    /// <summary>
    /// Asked once per pass and never allowed to fail the pass. A backup that refuses to
    /// run because it could not read a bucket setting is worse than an unversioned one —
    /// and several S3-compatible backends report versioning inconsistently or deny the
    /// call outright.
    /// </summary>
    public async Task<ObjectStorageVersioning> GetVersioningAsync(CancellationToken ct)
    {
        if (_client is null)
        {
            return ObjectStorageVersioning.Unknown;
        }

        try
        {
            var response = await _client.GetBucketVersioningAsync(new GetBucketVersioningRequest
            {
                BucketName = _options.Bucket,
            }, ct);

            return response.VersioningConfig?.Status == VersionStatus.Enabled
                ? ObjectStorageVersioning.Enabled
                : ObjectStorageVersioning.Disabled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "Could not read bucket versioning for {Bucket}; reporting it as unknown",
                _options.Bucket);

            return ObjectStorageVersioning.Unknown;
        }
    }

    private AmazonS3Client Require()
        => _client ?? throw new InvalidOperationException(
            "Backup:Bucket is not configured; check IsConfigured before using object storage.");

    public void Dispose() => _client?.Dispose();
}
