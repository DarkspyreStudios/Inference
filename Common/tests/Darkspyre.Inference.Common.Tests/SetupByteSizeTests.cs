using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Common.Tests;

public sealed class SetupByteSizeTests
{
    [Theory]
    [InlineData(487_601_967L, "465 MB")]
    public void FormatsBinaryUnitsWithAtMostOneDecimal(long bytes, string expected) =>
        Assert.Equal(expected, SetupByteSize.Format(bytes));
}
