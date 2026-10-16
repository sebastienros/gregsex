using System.Text.RegularExpressions;
using Xunit;

namespace GregsEx.Tests;

// Baseline cases for the future engine, currently evaluated with the .NET regex engine.
public class RegexBaselineTests
{
    [Theory]
    [InlineData("greg", "greg", true)]
    [InlineData("greg", "Greg", false)]
    [InlineData("^[a-z]+$", "greg", true)]
    [InlineData("^[a-z]+$", "greg123", false)]
    [InlineData("^$", "", true)]
    public void DotNetBaseline(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Regex.IsMatch(input, pattern));
    }
}
