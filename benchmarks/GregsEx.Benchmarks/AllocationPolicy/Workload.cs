using GregsEx;
namespace AllocationPolicy;
internal sealed class Workload(Func<long> cycle, Action? cleanup = null, int warmCount = 16384, object? metadata = null) : IDisposable
{
    internal int WarmCount => warmCount;
    internal object? Metadata => metadata;
    internal long Cycle() => cycle();
    internal void Prepare()
    {
        long expected = Cycle();
        for (int i = 0; i < warmCount; i++) Check.Equal(expected, Cycle());
    }
    public void Dispose() => cleanup?.Invoke();
}
internal static class Check
{
    internal static void That(bool condition, string? message = null)
    { if (!condition) throw new InvalidOperationException(message ?? "Prepared semantic assertion failed."); }
    internal static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
}

internal static class InputIdentity
{
 internal static object Chars(string text)=>new {units=text.Select(c=>(int)c).ToArray(),sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(text.AsSpan())))};
 internal static object Bytes(byte[] bytes)=>new {units=bytes.Select(b=>(int)b).ToArray(),sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))};
}
