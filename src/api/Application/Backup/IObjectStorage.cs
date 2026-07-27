namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>Whether the destination bucket keeps previous versions of an overwritten key.</summary>
public enum ObjectStorageVersioning
{
    /// <summary>Could not be determined — the credential may lack the permission to ask.</summary>
    Unknown,

    Enabled,

    Disabled,
}

/// <summary>
/// The bucket, as much of it as backup needs. Deliberately small: put an object, ask how
/// big a stored object is, copy one key to another, and ask whether versioning is on.
/// <para>
/// It exists as an interface mainly so the offload pass is testable without a bucket —
/// the alternative is a feature whose only test is a live S3 account.
/// </para>
/// </summary>
public interface IObjectStorage
{
    /// <summary>False when no bucket is configured, in which case offload is inert.</summary>
    bool IsConfigured { get; }

    /// <summary>Describes the destination for logs and the console, without secrets.</summary>
    string Describe();

    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);

    /// <summary>Size of a stored object, or null when it does not exist.</summary>
    Task<long?> GetLengthAsync(string key, CancellationToken ct);

    /// <summary>
    /// Reads an object back, or null when it does not exist. This is the recovery
    /// direction: an operator who has lost the database should not also have to find and
    /// download the artifact by hand before the console can offer it.
    /// </summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct);

    Task CopyAsync(string sourceKey, string destinationKey, CancellationToken ct);

    Task<ObjectStorageVersioning> GetVersioningAsync(CancellationToken ct);
}
