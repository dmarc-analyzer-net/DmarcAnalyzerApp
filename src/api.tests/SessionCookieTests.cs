using DmarcAnalyzer.Api.Application.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The session cookie's <c>Secure</c> flag follows the request scheme.
/// <para>
/// It used to be hard-coded true, which fails in a way that gives no clue what
/// happened: a browser silently discards a <c>Secure</c> cookie on a plain-HTTP
/// origin that is not localhost, so signing in returned 200 and the very next
/// request was unauthenticated. Verified in Chrome against
/// <c>http://10.17.20.20:8092</c> — login 200, <c>/auth/me</c> 401.
/// </para>
/// <para>
/// That is the normal way this gets run on a home server or NAS, and it is how
/// Umbrel and CasaOS serve apps, so it made the app effectively uninstallable
/// there.
/// </para>
/// </summary>
public sealed class SessionCookieTests
{
    private static HttpRequest Request(bool https)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = https ? "https" : "http";
        return context.Request;
    }

    [Fact]
    public void SecureOverHttps()
    {
        Assert.True(SessionCookie.Options(Request(https: true)).Secure);
    }

    [Fact]
    public void NotSecureOverPlainHttp()
    {
        // Not a downgrade so much as the only thing that works: marking it Secure
        // over HTTP does not protect the cookie, it discards it.
        Assert.False(SessionCookie.Options(Request(https: false)).Secure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EverythingElseHoldsRegardlessOfScheme(bool https)
    {
        var options = SessionCookie.Options(Request(https));

        Assert.True(options.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.SameSite);
        Assert.Equal("/", options.Path);
        Assert.Equal(TimeSpan.FromDays(7), options.MaxAge);
    }

    [Fact]
    public void ForwardedProtoIsWhatRestoresItBehindAProxy()
    {
        // UseForwardedHeaders rewrites Scheme from X-Forwarded-Proto before the
        // endpoint runs, so this is the same object the login path would see
        // behind a correctly configured TLS proxy.
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        // Without the middleware, the header alone changes nothing — which is the
        // point of documenting Network__UseForwardedHeaders alongside this.
        Assert.False(SessionCookie.Options(context.Request).Secure);

        context.Request.Scheme = "https"; // what the middleware does
        Assert.True(SessionCookie.Options(context.Request).Secure);
    }
}
