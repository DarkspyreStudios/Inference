using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.STT.Whisper.Tests;

public sealed class WhisperPcmAndStorageTests
{
    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(192000)]
    public void DownmixAndResamplingProduceFiniteMono16kWithoutMutatingInput(int rate)
    {
        float[] input = Enumerable.Range(0, rate * 2).Select(index => index % 2 == 0 ? 0.25f : 0.75f).ToArray();
        float[] output = WhisperPcm.Normalize(new(input, rate, 2), default);
        Assert.InRange(output.Length, 15998, 16002);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
        Assert.InRange(output[8000], 0.49f, 0.51f);
        Assert.Equal(0.25f, input[0]); Assert.Equal(0.75f, input[1]);
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(7999, 1, 2)]
    [InlineData(192001, 1, 2)]
    [InlineData(16000, 0, 2)]
    [InlineData(16000, 9, 18)]
    [InlineData(16000, 2, 3)]
    public void MalformedPcmFailsBeforeNativeExecution(int rate, int channels, int samples)
    {
        Assert.Equal(InferenceFailureCode.InvalidRequest,
            Assert.Throws<InferenceException>(() => WhisperPcm.Normalize(new(new float[samples], rate, channels), default)).Code);
    }

    [Fact]
    public void NonfinitePcmIsRejectedAndCancellationIsPreserved()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<InferenceException>(() => WhisperPcm.Normalize(new(new[] { value }, 16000, 1), default));
        Assert.ThrowsAny<OperationCanceledException>(() => WhisperPcm.Normalize(new(new float[100], 16000, 1), new(true)));
        Assert.Empty(WhisperPcm.Normalize(new(Array.Empty<float>(), 16000, 1), default));
    }

    [Fact]
    public async Task TheInstalledFileIsUsedInPlaceAndHeldOpenUntilDisposal()
    {
        using var directory = new WhisperFixture.PackDirectory();
        string path = Path.Combine(directory.Path, "model.bin");
        byte[] bytes = WhisperFixture.Header();
        await File.WriteAllBytesAsync(path, bytes);

        WhisperModelFile file = await WhisperModelFile.OpenAsync(path, bytes.Length, default);
        Assert.Equal(path, file.Path);
        Assert.Throws<IOException>(() => WhisperFixture.AssertNoOpenHandle(path));
        await file.DisposeAsync();
        WhisperFixture.AssertNoOpenHandle(path);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task AChangedLengthIsRejectedAndTheFileIsClosed(int adjustment)
    {
        using var directory = new WhisperFixture.PackDirectory();
        string path = Path.Combine(directory.Path, "model.bin");
        byte[] bytes = WhisperFixture.Header();
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => WhisperModelFile.OpenAsync(path, bytes.Length + adjustment, default));
        WhisperFixture.AssertNoOpenHandle(path);
    }

    [Fact]
    public void MalformedWhisperHeaderCannotReachNativeModelAllocation()
    {
        foreach (int position in Enumerable.Range(0, 12))
        {
            byte[] header = WhisperFixture.Header(); BitConverter.GetBytes(-1).CopyTo(header, position * 4);
            Assert.Throws<InvalidDataException>(() => WhisperCompatibility.Inspect(new MemoryStream(header)));
        }
        Assert.Throws<EndOfStreamException>(() => WhisperCompatibility.Inspect(new MemoryStream(new byte[3])));
    }
}
