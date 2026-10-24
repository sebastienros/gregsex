using Xunit;

namespace GregsEx.Tests.Packaging;

public class StrongNameTests
{
    // Strong-named consumers (TextMateSharp) load GregsEx on .NET Framework only if it is strong-named too, and its
    // identity must stay the same key across releases.
    [Fact]
    public void GregsExIsStrongNamedWithTheRepositoryKey()
    {
        var token = typeof(RegexPattern).Assembly.GetName().GetPublicKeyToken();
        Assert.Equal("172acb106fb17aad", Convert.ToHexString(token!).ToLowerInvariant());
    }
}
