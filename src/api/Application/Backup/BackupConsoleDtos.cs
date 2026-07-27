namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// What the console needs before it will offer an import, from a GET that cannot carry an
/// artifact of its own.
/// <para>
/// An uploaded file is parsed and checked in the browser against these facts, which is why
/// no artifact reaches this endpoint: an invalid file, a format-version mismatch or a
/// wrong-key mismatch is then caught before anything is sent.
/// </para>
/// </summary>
/// <param name="IsEmptyInstall">
/// No clients and no domains. The console's only authenticated clean-install signal:
/// <c>GET /api/v1/auth/setup</c> cannot answer it, because by the time the console loads the
/// bootstrap administrator exists and <c>requiresBootstrap</c> is already false.
/// </param>
/// <param name="SupportedFormatVersion">
/// The version <em>this build</em> reads, not any artifact's. The console refuses a file
/// declaring anything else rather than uploading it and guessing at the difference.
/// </param>
/// <param name="KeyFingerprint">
/// Fingerprint of the running credential key, or null when none is configured. Compared
/// against an artifact's manifest to answer "do I hold the right key for this file?" before
/// the import rather than at the next failed mailbox sync.
/// </param>
/// <param name="BucketConfigured">
/// Distinguishes "no object storage" from "object storage with nothing in it" — the second
/// is a real warning, because it means offload is on and has never succeeded.
/// </param>
public sealed record ConfigImportPreviewDto(
    bool IsEmptyInstall,
    int SupportedFormatVersion,
    string? KeyFingerprint,
    bool BucketConfigured,
    ConfigImportBucketArtifactDto? Bucket);

/// <summary>
/// The artifact found in object storage.
/// <para>
/// There is deliberately no <c>credentialsProtected</c> here: offload refuses to run without
/// a credential encryption key, so an artifact that reached the bucket cannot be one of the
/// plaintext ones. Only an uploaded file can be.
/// </para>
/// </summary>
/// <param name="CarriesSignedInUser">
/// True when the acting operator's email appears in the artifact's users. On an email
/// collision the imported user wins, so importing replaces their own password and ends this
/// session — they have to be told before, not after.
/// </param>
public sealed record ConfigImportBucketArtifactDto(
    string Key,
    int FormatVersion,
    DateTime ExportedAtUtc,
    bool KeyFingerprintMatches,
    bool CarriesSignedInUser,
    IReadOnlyList<ConfigImportEntityCountDto> Entities);

public sealed record ConfigImportEntityCountDto(string Entity, int InArtifact);

/// <summary>
/// An import's outcome as the console reads it.
/// <para>
/// Adapts <see cref="BackupImportResult"/>: the service reports per-table detail, and the
/// console needs two totals plus the two facts that decide what it may do next — whose
/// passwords changed, and whether this session is one of them.
/// </para>
/// </summary>
/// <param name="SignedInSessionInvalidated">
/// Load-bearing. Every later request from this tab will 401, and a 401 force-logs-out the
/// whole console, so this response is the last thing the operator sees — it has to carry the
/// credentials they need next.
/// </param>
public sealed record ConfigImportResponseDto(
    bool DryRun,
    string Mode,
    int Created,
    int Updated,
    bool MailboxCredentialsWillNotDecrypt,
    bool SignedInSessionInvalidated,
    IReadOnlyList<ConfigImportEntityResultDto> Entities,
    IReadOnlyList<string> UsersWithChangedPasswords,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings);

public sealed record ConfigImportEntityResultDto(
    string Entity,
    int Created,
    int Updated,
    int Skipped);
