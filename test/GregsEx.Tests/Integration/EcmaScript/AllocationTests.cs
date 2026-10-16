using Xunit;

namespace GregsEx.Tests.Integration.EcmaScript;

// Must run only as a prepared granted host phase; ordinary semantic filters exclude this class.
public sealed class AllocationTests
{
    [Fact]
    public void PreparedPublicGenericAndTypedOperationsAllocateExactlyZero()
        => Assert.Equal(36, EcmaSmoke.Run(measure: true, Console.WriteLine));
}
