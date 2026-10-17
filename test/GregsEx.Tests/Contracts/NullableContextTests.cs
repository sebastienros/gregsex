using Xunit;

namespace GregsEx.Tests.Contracts;

// The library's sources must compile whatever nullable context they are compiled in: a source-linked, vendored or
// generated-code compilation (where the project-wide <Nullable> does not apply) otherwise fails with CS8669 on the
// first annotation. So every source declares its own context, and the table generators emit the directive too.
public sealed class NullableContextTests
{
    [Fact]
    public void EverySourceDeclaresItsNullableContext()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GregsEx.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var source = Path.Combine(root!.FullName, "src", "GregsEx");
        var missing = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !File.ReadLines(path).First().TrimStart('\uFEFF').StartsWith("#nullable enable", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(source, path))
            .ToList();
        Assert.True(missing.Count == 0, "Missing '#nullable enable' on the first line: " + string.Join(", ", missing));
    }
}
