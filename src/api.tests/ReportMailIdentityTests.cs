using DmarcAnalyzer.Api.Application.Backup;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// How an archived message is named, now that two protocols name messages differently.
/// <para>
/// Both halves of this matter for the same reason: the sync pass writes the key and the
/// retention pass looks it up, and a key that disagrees between them means "not archived" for
/// every message — which reads as the safety rule working when it is really a bug that stops
/// mailbox retention dead.
/// </para>
/// </summary>
public sealed class ReportMailIdentityTests
{
    private static readonly Guid SourceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime At = new(2026, 7, 27, 6, 11, 0, DateTimeKind.Utc);

    /// <summary>
    /// The IMAP key predates POP3 and mail is already archived under it, so it has to survive
    /// the generalisation byte for byte. A bucket full of messages that suddenly read as
    /// unarchived would silently suspend every deletion pass.
    /// </summary>
    [Fact]
    public void TheImapKeyIsUnchanged()
    {
        var key = ReportMailArchive.Key("dmarc", SourceId, ReportMailIdentity.ForImap(4711, 9), At);

        Assert.Equal($"dmarc/reports/2026/07/27/{SourceId}/9-4711.eml.gz", key);
    }

    /// <summary>
    /// POP3 has no UIDVALIDITY, and a literal in its place is also what stops a UIDL that
    /// happens to look like a number from colliding with an IMAP key.
    /// </summary>
    [Fact]
    public void APop3KeyIsNamespacedAwayFromImap()
    {
        var pop3 = ReportMailArchive.Key("dmarc", SourceId, ReportMailIdentity.ForPop3("9"), At);
        var imap = ReportMailArchive.Key("dmarc", SourceId, ReportMailIdentity.ForImap(9, 9), At);

        Assert.Equal($"dmarc/reports/2026/07/27/{SourceId}/pop3-9.eml.gz", pop3);
        Assert.NotEqual(imap, pop3);
    }

    [Fact]
    public void AnOrdinaryUidlIsUsedVerbatim()
    {
        Assert.Equal(new ReportMailIdentity("pop3", "whqtswO00WBw418f9t5JxYwZ"),
            ReportMailIdentity.ForPop3("whqtswO00WBw418f9t5JxYwZ"));
    }

    /// <summary>
    /// RFC 1939 allows any printable ASCII in a UIDL, including characters that mean
    /// something in an object key. A <c>/</c> left verbatim would push the object into a
    /// prefix nobody documented, where a lifecycle rule written against the documented layout
    /// would never expire it.
    /// </summary>
    [Theory]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("../escape")]
    [InlineData("%2F")]
    public void AUidlThatWouldReshapeTheKeyIsHashedInstead(string uidl)
    {
        var identity = ReportMailIdentity.ForPop3(uidl);

        Assert.StartsWith("h-", identity.Uid);
        Assert.DoesNotContain('/', identity.Uid);

        // Deterministic, because ExistsAsync recomputes the key rather than remembering it.
        Assert.Equal(identity, ReportMailIdentity.ForPop3(uidl));
    }

    /// <summary>
    /// Hashing must not merge two messages into one key: whichever was archived second would
    /// authorise deleting the first.
    /// </summary>
    [Fact]
    public void HashedUidlsStayDistinct()
    {
        Assert.NotEqual(ReportMailIdentity.ForPop3("a/b"), ReportMailIdentity.ForPop3("a/c"));
    }

    /// <summary>
    /// 70 characters is the RFC limit; a server that exceeds it gets hashed rather than
    /// trusted, since the column that holds the checkpoint stops at 70 too.
    /// </summary>
    [Fact]
    public void AnOverlongUidlIsHashed()
    {
        Assert.StartsWith("h-", ReportMailIdentity.ForPop3(new string('a', 71)).Uid);
        Assert.DoesNotContain("h-", ReportMailIdentity.ForPop3(new string('a', 70)).Uid);
    }
}
