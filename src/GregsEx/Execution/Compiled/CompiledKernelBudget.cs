using System.Diagnostics.CodeAnalysis;

namespace GregsEx.Execution.Compiled;

// Process-wide ceiling on the estimated size of live runtime-compiled kernels. Runtime compilation cannot be cancelled
// and its output lives until the pattern is collected, so a caller compiling many untrusted patterns must hit a
// deterministic NotSupportedException instead of unbounded memory growth. Leases release on finalization.
internal static class CompiledKernelBudget
{
    internal const long CeilingBytes = 64L * 1024 * 1024;
    private static long s_live;

    internal static long LiveBytes => Interlocked.Read(ref s_live);

    internal static bool TryReserve(long bytes, [NotNullWhen(true)] out Lease? lease)
    {
        lease = null;
        var after = Interlocked.Add(ref s_live, bytes);
        if (after > CeilingBytes)
        {
            Interlocked.Add(ref s_live, -bytes);
            return false;
        }
        lease = new Lease(bytes);
        return true;
    }

    internal sealed class Lease : IDisposable
    {
        private long _bytes;
        private readonly long _estimate;
        internal Lease(long bytes) { _bytes = bytes; _estimate = bytes; }
        internal long Estimate => _estimate;

        public void Dispose()
        {
            var bytes = Interlocked.Exchange(ref _bytes, 0);
            if (bytes != 0)
            {
                Interlocked.Add(ref s_live, -bytes);
            }

            GC.SuppressFinalize(this);
        }

        ~Lease()
        {
            var bytes = Interlocked.Exchange(ref _bytes, 0);
            if (bytes != 0)
            {
                Interlocked.Add(ref s_live, -bytes);
            }
        }
    }
}
