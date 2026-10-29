using System.IO;
using System.Reflection;
using Xunit;

namespace GregsEx.Framework.Tests;

// GregsEx.Generator also runs in .NET Framework hosts: Visual Studio's analyzer host, and third-party TextMate grammar
// generators that load GregsEx.Generator.dll and call KernelSource.TryEmit through reflection, as this test does.
public sealed class KernelSourceTests
{
    private const int TextMate = 8; // OnigurumaOptions.CaptureGroup

    [Theory]
    [InlineData("[a-z]+(ing|ed)")]
    [InlineData(@"(?<word>a(?:\g<word>)?b)")]
    public void Kernels_are_emitted_on_framework(string source)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "generator", "GregsEx.Generator.dll"));
        var tryEmit = assembly.GetType("GregsEx.SourceGeneration.KernelSource", throwOnError: true)!
            .GetMethod("TryEmit", BindingFlags.Public | BindingFlags.Static)!;
        var options = Enum.ToObject(assembly.GetType("GregsEx.OnigurumaOptions", throwOnError: true)!, TextMate);

        var result = tryEmit.Invoke(null, [source, options, "Plain", "Capture", null])!;

        Assert.Equal("Succeeded", result.GetType().GetProperty("Status")!.GetValue(result)!.ToString());
        Assert.NotNull(result.GetType().GetProperty("PlainClass")!.GetValue(result));
    }
}
