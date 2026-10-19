using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Common.Tests;

public sealed class SetupByteSizeTests
{
    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(487_601_967L, "465 MB")]
    public void FormatsBinaryUnitsWithAtMostOneDecimal(long bytes, string expected) =>
        Assert.Equal(expected, SetupByteSize.Format(bytes));
}
