using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Common.Tests;

public sealed class SetupByteSizeTests
{
    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(487_601_967L, "465 MB")]
    [InlineData(1_610_612_736L, "1.5 GB")]
    public void FormatsBinaryUnitsWithAtMostOneDecimal(long bytes, string expected) =>
        Assert.Equal(expected, SetupByteSize.Format(bytes));

    [Fact]
    public void ANegativeSizeThrows() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupByteSize.Format(-1));
}
