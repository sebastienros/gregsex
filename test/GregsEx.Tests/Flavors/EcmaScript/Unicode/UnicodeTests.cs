using Xunit;
namespace GregsEx.Tests.Flavors.EcmaScript.Unicode;

public sealed class UnicodeTests
{
    [Theory]
    [InlineData(UnicodeVersion.Unicode16_0)]
    [InlineData(UnicodeVersion.Unicode17_0)]
    public void PinnedInventoryBoundariesAliasesCaseAndAdmission(UnicodeVersion version) => UnicodeChecks.Run(version);
}
