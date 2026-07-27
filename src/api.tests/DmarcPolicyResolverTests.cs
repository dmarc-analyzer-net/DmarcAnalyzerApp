using DmarcAnalyzer.Api.Application.Analytics;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// A subdomain publishing no DMARC record of its own is not unprotected — RFC 7489 §6.6.3 has
/// the receiver apply the organisational domain's sp=, or its p= when there is no sp=. Before
/// this walk existed, one real instance reported six domains as having no policy while five of
/// them were covered by p=reject, so the console called them unprotected while receivers were
/// rejecting on their behalf.
/// </summary>
public sealed class DmarcPolicyResolverTests
{
    private static DmarcPolicyResolver Resolver(TestDnsTxtResolver dns) => new(dns);

    [Fact]
    public async Task OwnRecordWins_AndIsNotMarkedInherited()
    {
        var dns = new TestDnsTxtResolver()
            .Publish("_dmarc.mail.acme.example", "v=DMARC1; p=quarantine")
            .Publish("_dmarc.acme.example", "v=DMARC1; p=reject");

        var result = await Resolver(dns).ResolveAsync("mail.acme.example", CancellationToken.None);

        Assert.Equal(RecordLookupStatus.Found, result.Status);
        Assert.Equal("quarantine", result.Policy);
        Assert.Null(result.InheritedFrom);
    }

    /// <summary>
    /// The case that matters: gitlab.yulsn.io publishes p=none beneath a p=reject parent. A
    /// subdomain opting out of its parent's enforcement must not be reported as enforced.
    /// </summary>
    [Fact]
    public async Task AnOwnRecordThatWeakensThePolicyIsRespected()
    {
        var dns = new TestDnsTxtResolver()
            .Publish("_dmarc.gitlab.acme.example", "v=DMARC1; p=none")
            .Publish("_dmarc.acme.example", "v=DMARC1; p=reject");

        var result = await Resolver(dns).ResolveAsync("gitlab.acme.example", CancellationToken.None);

        Assert.Equal("none", result.Policy);
        Assert.Equal(RecordLookupStatus.Found, result.Status);
    }

    [Fact]
    public async Task WithNoOwnRecord_InheritsTheParentPolicy()
    {
        var dns = new TestDnsTxtResolver().Publish("_dmarc.acme.example", "v=DMARC1; p=reject");

        var result = await Resolver(dns).ResolveAsync("client.acme.example", CancellationToken.None);

        Assert.Equal(RecordLookupStatus.Inherited, result.Status);
        Assert.Equal("reject", result.Policy);
        Assert.Equal("acme.example", result.InheritedFrom);
    }

    /// <summary>sp= exists to say what subdomains get, so it beats p= for an inheritor.</summary>
    [Fact]
    public async Task SubdomainPolicyBeatsPolicyWhenInheriting()
    {
        var dns = new TestDnsTxtResolver()
            .Publish("_dmarc.acme.example", "v=DMARC1; p=reject; sp=quarantine");

        var result = await Resolver(dns).ResolveAsync("client.acme.example", CancellationToken.None);

        Assert.Equal("quarantine", result.Policy);
        Assert.Equal("acme.example", result.InheritedFrom);
    }

    /// <summary>An sp= of none is a deliberate exemption and must be honoured, not ignored.</summary>
    [Fact]
    public async Task AnSpOfNoneIsInherited()
    {
        var dns = new TestDnsTxtResolver()
            .Publish("_dmarc.acme.example", "v=DMARC1; p=reject; sp=none");

        var result = await Resolver(dns).ResolveAsync("client.acme.example", CancellationToken.None);

        Assert.Equal("none", result.Policy);
        Assert.Equal(RecordLookupStatus.Inherited, result.Status);
    }

    [Fact]
    public async Task WalksPastAnIntermediateLevelThatPublishesNothing()
    {
        var dns = new TestDnsTxtResolver().Publish("_dmarc.acme.example", "v=DMARC1; p=reject");

        var result = await Resolver(dns).ResolveAsync("smtp.eu.mail.acme.example", CancellationToken.None);

        Assert.Equal("reject", result.Policy);
        Assert.Equal("acme.example", result.InheritedFrom);
    }

    /// <summary>The nearest ancestor with a record wins, as it would for a receiver.</summary>
    [Fact]
    public async Task TheNearestAncestorWins()
    {
        var dns = new TestDnsTxtResolver()
            .Publish("_dmarc.mail.acme.example", "v=DMARC1; p=quarantine")
            .Publish("_dmarc.acme.example", "v=DMARC1; p=reject");

        var result = await Resolver(dns).ResolveAsync("smtp.mail.acme.example", CancellationToken.None);

        Assert.Equal("quarantine", result.Policy);
        Assert.Equal("mail.acme.example", result.InheritedFrom);
    }

    [Fact]
    public async Task WithNothingAnywhere_IsMissing()
    {
        var result = await Resolver(TestDnsTxtResolver.Empty())
            .ResolveAsync("client.acme.example", CancellationToken.None);

        Assert.Equal(RecordLookupStatus.Missing, result.Status);
        Assert.Null(result.Policy);
        Assert.Null(result.InheritedFrom);
    }

    /// <summary>
    /// A failed lookup on the domain itself says nothing about its ancestors, and inventing an
    /// answer from one would be worse than admitting we do not know. Callers keep the last
    /// known value on this status rather than blanking a policy on a transient SERVFAIL.
    /// </summary>
    [Fact]
    public async Task AFailedLookupOnTheDomainDoesNotWalkUp()
    {
        var dns = new TestDnsTxtResolver()
            .FailFor("_dmarc.client.acme.example")
            .Publish("_dmarc.acme.example", "v=DMARC1; p=reject");

        var result = await Resolver(dns).ResolveAsync("client.acme.example", CancellationToken.None);

        Assert.Equal(RecordLookupStatus.LookupFailed, result.Status);
        Assert.Null(result.Policy);
    }

    /// <summary>But one flaky level partway up must not hide a policy published above it.</summary>
    [Fact]
    public async Task AFailedLookupPartwayUpKeepsWalking()
    {
        var dns = new TestDnsTxtResolver()
            .FailFor("_dmarc.mail.acme.example")
            .Publish("_dmarc.acme.example", "v=DMARC1; p=reject");

        var result = await Resolver(dns).ResolveAsync("smtp.mail.acme.example", CancellationToken.None);

        Assert.Equal(RecordLookupStatus.Inherited, result.Status);
        Assert.Equal("acme.example", result.InheritedFrom);
    }

    /// <summary>
    /// A TLD is never an organisational domain. Without this the walk would query
    /// _dmarc.example for anything under example.com and, worse, could inherit from it.
    /// </summary>
    [Theory]
    [InlineData("acme.example", new string[0])]
    [InlineData("mail.acme.example", new[] { "acme.example" })]
    [InlineData("smtp.mail.acme.example", new[] { "mail.acme.example", "acme.example" })]
    [InlineData("example", new string[0])]
    public void AncestorsStopBeforeSingleLabelNames(string name, string[] expected)
        => Assert.Equal(expected, DmarcPolicyResolver.Ancestors(name).ToArray());

    /// <summary>
    /// Bounded, as DMARCbis specifies, rather than walking to the root: a pathological name
    /// must not turn one page view into a dozen DNS lookups.
    /// </summary>
    [Fact]
    public void TheWalkIsBounded()
    {
        var deep = string.Join('.', Enumerable.Range(0, 12).Select(i => $"l{i}")) + ".acme.example";

        Assert.Equal(5, DmarcPolicyResolver.Ancestors(deep).Count());
    }
}
