using System.Reflection;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Assemblies the AWS SDK loads by reflection, so nothing fails at build time when they are
/// missing — only at runtime, on the first S3 call of a deployment that uses them.
/// <para>
/// An S3 report source with empty key fields uses the ambient credential chain. On EKS with
/// IRSA that chain resolves to web identity credentials, and the SDK then loads
/// <c>AWSSDK.SecurityToken</c> to call STS <c>AssumeRoleWithWebIdentity</c>. Without the
/// package every sync run fails with "Assembly AWSSDK.SecurityToken could not be found or
/// loaded. This assembly must be available at runtime to use
/// Amazon.Runtime.AssumeRoleAWSCredentials", although the docs recommend IRSA.
/// </para>
/// </summary>
public sealed class AwsSdkRuntimeDependencyTests
{
    [Fact]
    public void SecurityTokenAssemblyIsAvailableForWebIdentityCredentials()
    {
        var assembly = Assembly.Load("AWSSDK.SecurityToken");

        Assert.NotNull(assembly.GetType("Amazon.SecurityToken.AmazonSecurityTokenServiceClient"));
    }
}
