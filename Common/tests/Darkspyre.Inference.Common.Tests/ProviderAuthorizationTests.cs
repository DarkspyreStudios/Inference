namespace Darkspyre.Inference.Common.Tests;

public sealed class ProviderAuthorizationTests
{
    [Fact]
    public void DiagnosticTextNeverContainsHeaderFields()
    {
        var header = new ProviderAuthorizationHeader("protected-scheme", "protected-value");
        Assert.Equal("protected-scheme", header.Scheme);
        Assert.Equal("protected-value", header.Value);
        Assert.Equal("ProviderAuthorizationHeader { Redacted }", header.ToString());
        Assert.DoesNotContain("protected", $"{header}", StringComparison.Ordinal);
    }
}
