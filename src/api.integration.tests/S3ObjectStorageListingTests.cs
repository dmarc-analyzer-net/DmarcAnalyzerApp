using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using DmarcAnalyzer.Api.Application.Backup;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// <see cref="S3ObjectStorage.ListAsync"/> against a real S3-compatible server, for the
/// same reason <see cref="S3ReportSourceTests"/> uses one: what is under test is the
/// agreement with the service — that a listing is in key order, that <c>StartAfter</c>
/// resumes after a key, that a continuation token pages, and that directory markers are
/// dropped — which a mocked client would only assert.
/// </summary>
public sealed class S3ObjectStorageListingTests : IAsyncLifetime
{
    private const int S3Port = 7070;
    private const string AccessKey = "dmarcanalyzer";
    private const string SecretKey = "dmarcanalyzer-secret";
    private const string Bucket = "backup";

    // Same image and digest as S3ReportSourceTests; see there for why it is pinned.
    private readonly IContainer _s3Server = new ContainerBuilder(
            "ghcr.io/versity/versitygw:v1.8.0@sha256:30292fc2eeacc67a36993b01f7a7a5e3361a19cced0e80c1d71cfa2a4b0a2499")
        .WithEnvironment("ROOT_ACCESS_KEY", AccessKey)
        .WithEnvironment("ROOT_SECRET_KEY", SecretKey)
        .WithCommand("--health", "/_/health", "posix", "/data")
        .WithTmpfsMount("/data")
        .WithPortBinding(S3Port, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
            r.ForPort(S3Port).ForPath("/_/health")))
        .Build();

    private AmazonS3Client _s3 = null!;
    private S3ObjectStorage _storage = null!;

    private string Endpoint => $"http://{_s3Server.Hostname}:{_s3Server.GetMappedPublicPort(S3Port)}";

    public async Task InitializeAsync()
    {
        await _s3Server.StartAsync();

        _s3 = new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config
        {
            ServiceURL = Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });
        await _s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });

        _storage = new S3ObjectStorage(
            Options.Create(new BackupOptions
            {
                Bucket = Bucket,
                Endpoint = Endpoint,
                AccessKeyId = AccessKey,
                SecretAccessKey = SecretKey,
                ForcePathStyle = true,
                Prefix = "dmarc",
            }),
            NullLogger<S3ObjectStorage>.Instance);
    }

    public async Task DisposeAsync()
    {
        _storage?.Dispose();
        _s3?.Dispose();
        await _s3Server.DisposeAsync();
    }

    private async Task PutAsync(string key, string content = "x")
        => await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            DisablePayloadSigning = false,
        });

    [Fact]
    public async Task Lists_InKeyOrder_UnderThePrefix_ResumingAfterAKey()
    {
        await PutAsync("dmarc/reports/2026/10/02/src/1-20.eml.gz");
        await PutAsync("dmarc/reports/2026/10/01/src/1-10.eml.gz");
        await PutAsync("dmarc/reports/2026/10/01/src/1-11.eml.gz");
        await PutAsync("dmarc/config/latest.json");

        var all = await _storage.ListAsync("dmarc/reports/", null, null, CancellationToken.None);

        Assert.Null(all.NextContinuationToken);
        Assert.Equal(
            [
                "dmarc/reports/2026/10/01/src/1-10.eml.gz",
                "dmarc/reports/2026/10/01/src/1-11.eml.gz",
                "dmarc/reports/2026/10/02/src/1-20.eml.gz",
            ],
            all.Objects.Select(x => x.Key));
        Assert.All(all.Objects, x => Assert.True(x.Size > 0));
        Assert.All(all.Objects, x => Assert.Equal(DateTimeKind.Utc, x.LastModifiedUtc.Kind));

        var resumed = await _storage.ListAsync(
            "dmarc/reports/", "dmarc/reports/2026/10/01/src/1-10.eml.gz", null, CancellationToken.None);

        Assert.Equal(
            [
                "dmarc/reports/2026/10/01/src/1-11.eml.gz",
                "dmarc/reports/2026/10/02/src/1-20.eml.gz",
            ],
            resumed.Objects.Select(x => x.Key));
    }

    /// <summary>
    /// S3 pages at 1000 keys. A thousand and one objects, one request per page, and the
    /// token carries the walk over the boundary without a repeat or a gap.
    /// </summary>
    [Fact]
    public async Task Pages_OnTheContinuationToken_WithoutRepeatingOrSkipping()
    {
        const int count = 1001;
        var expected = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var key = $"dmarc/reports/2026/10/01/src/1-{i:D5}.eml.gz";
            expected.Add(key);
            await PutAsync(key);
        }

        var seen = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var page = await _storage.ListAsync("dmarc/reports/", null, token, CancellationToken.None);
            pages++;
            seen.AddRange(page.Objects.Select(x => x.Key));
            token = page.NextContinuationToken;
        }
        while (token is not null);

        Assert.Equal(2, pages);
        Assert.Equal(expected, seen);
    }
}
