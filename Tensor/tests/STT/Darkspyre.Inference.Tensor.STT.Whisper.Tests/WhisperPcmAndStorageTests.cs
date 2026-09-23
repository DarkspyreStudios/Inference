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
    public async Task NonFileStoreProjectionKeepsSourceAliveAndDeletesOnlyItsPrivateFile()
    {
        var store = new WhisperFixture.Store();
        byte[] bytes = WhisperFixture.Header();
        await store.SaveAsync("models", "weights", new MemoryStream(bytes));
        WhisperModelFile file = await WhisperModelFile.OpenAsync(new(store, "models", "weights"), bytes.Length, default);
        string path = file.Path;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.True(store.LastRead!.CanRead);
        await file.DisposeAsync();
        Assert.False(store.LastRead.CanRead); Assert.False(File.Exists(path)); Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.True(await store.ExistsAsync("models", "weights"));
    }

    [Fact]
    public async Task DirectFileStoreRetainsOriginalFileWithoutCopyOrDeletion()
    {
        string root = Directory.CreateTempSubdirectory("whisper-direct-test-").FullName;
        try
        {
            var store = new TensorDirectFilePersistStore(root);
            byte[] bytes = WhisperFixture.Header();
            await store.SaveAsync("models", "weights", new MemoryStream(bytes));
            WhisperModelFile file = await WhisperModelFile.OpenAsync(new(store, "models", "weights"), bytes.Length, default);
            string path = file.Path;
            Assert.StartsWith(root, path, StringComparison.Ordinal);
            await file.DisposeAsync(); Assert.True(File.Exists(path));
            await store.DeleteAsync("models", "weights");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ProjectionRejectsChangedLengthAndClosesSource(int adjustment)
    {
        var store = new WhisperFixture.Store(); byte[] bytes = WhisperFixture.Header();
        await store.SaveAsync("models", "weights", new MemoryStream(bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => WhisperModelFile.OpenAsync(new(store, "models", "weights"), bytes.Length + adjustment, default));
        Assert.False(store.LastRead!.CanRead);
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
