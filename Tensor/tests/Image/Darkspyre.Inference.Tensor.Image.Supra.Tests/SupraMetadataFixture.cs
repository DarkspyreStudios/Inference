using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Image.Supra.Tests;

internal sealed class SupraMetadataFixture
{
    public sealed record Tensor(string Dtype, long[] Shape);
    public Dictionary<string, long> IntegerValues { get; init; } = [];
    public Dictionary<string, Tensor> Dit { get; init; } = [];
    public Dictionary<string, Tensor> Text { get; init; } = [];
    public Dictionary<string, Tensor> Vae { get; init; } = [];
    internal static SupraMetadataFixture Read() => JsonSerializer.Deserialize<SupraMetadataFixture>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "supra-metadata.json")))!;
    internal IReadOnlyDictionary<string, TorchTensorInfo> DitInfo() => Dit.ToDictionary(item => item.Key,
        item => new TorchTensorInfo { Name = item.Key, Dtype = Enum.Parse<TorchStorageDtype>(item.Value.Dtype), Shape = item.Value.Shape });
    internal static IReadOnlyDictionary<string, SafetensorTensorInfo> SafeInfo(Dictionary<string, Tensor> tensors) => tensors.ToDictionary(item => item.Key,
        item => new SafetensorTensorInfo { Name = item.Key, Dtype = Enum.Parse<SafetensorDtype>(item.Value.Dtype), Shape = item.Value.Shape });

    internal static byte[] Safetensors(Dictionary<string, Tensor> tensors)
    {
        long offset = 0;
        var header = new Dictionary<string, object>();
        foreach ((string name, Tensor tensor) in tensors)
        {
            long length = Elements(tensor) * (tensor.Dtype is "F16" or "BF16" ? 2 : 4);
            header[name] = new { dtype = tensor.Dtype, shape = tensor.Shape, data_offsets = new[] { offset, offset + length } };
            offset += length;
        }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);
        return [.. BitConverter.GetBytes((ulong)json.Length), .. json];
    }

    internal byte[] TorchZip()
    {
        using var pickle = new MemoryStream();
        pickle.Write([0x80, 0x02, (byte)'}', (byte)'(']);
        Unicode(pickle, "model"); pickle.Write([(byte)'}', (byte)'(']);
        int index = 0;
        var lengths = new Dictionary<string, uint>();
        foreach ((string name, Tensor tensor) in Dit.Where(item => !item.Key.StartsWith("config.", StringComparison.Ordinal)))
        { Unicode(pickle, name); WriteTensor(pickle, tensor, index++, lengths); }
        pickle.WriteByte((byte)'u');
        Unicode(pickle, "config"); pickle.Write([(byte)'}', (byte)'(']);
        foreach ((string name, long value) in IntegerValues) { Unicode(pickle, name); Integer(pickle, checked((int)value)); }
        foreach ((string name, Tensor tensor) in Dit.Where(item => item.Key.StartsWith("config.", StringComparison.Ordinal)))
        { Unicode(pickle, name[7..]); WriteTensor(pickle, tensor, index++, lengths); }
        pickle.Write([(byte)'u', (byte)'u', (byte)'.']);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(archive, "fixture/byteorder", "little"u8.ToArray());
            Entry(archive, "fixture/data.pkl", pickle.ToArray());
            foreach (string name in lengths.Keys) { Entry(archive, name, []); }
        }
        byte[] bytes = output.ToArray();
        // A sparse synthetic archive advertises storage lengths, but contains no weight payloads.
        // InspectMetadata never opens these entries. Any accidental weight read would fail.
        for (int position = 0; position <= bytes.Length - 46; position++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position)) != 0x02014b50) { continue; }
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 28));
            string name = Encoding.UTF8.GetString(bytes, position + 46, nameLength);
            if (lengths.TryGetValue(name, out uint length)) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position + 24), length); }
        }
        return bytes;
    }

    private static long Elements(Tensor tensor) => tensor.Shape.Aggregate(1L, (count, dimension) => checked(count * dimension));
    private static void WriteTensor(Stream output, Tensor tensor, int index, Dictionary<string, uint> lengths)
    {
        string key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string storage = tensor.Dtype switch { "Float32" => "FloatStorage", "BFloat16" => "BFloat16Storage", "Int64" => "LongStorage", _ => throw new InvalidOperationException() };
        lengths["fixture/data/" + key] = checked((uint)(Elements(tensor) * (storage == "LongStorage" ? 8 : storage == "BFloat16Storage" ? 2 : 4)));
        Global(output, "torch._utils", "_rebuild_tensor_v2"); output.Write([(byte)'(', (byte)'(']);
        Unicode(output, "storage"); Global(output, "torch", storage); Unicode(output, key); Unicode(output, "cpu");
        Integer(output, checked((int)Elements(tensor))); output.Write([(byte)'t', (byte)'Q']); Integer(output, 0);
        Tuple(output, tensor.Shape);
        long[] stride = new long[tensor.Shape.Length]; long running = 1;
        for (int dimension = stride.Length - 1; dimension >= 0; dimension--) { stride[dimension] = running; running *= tensor.Shape[dimension]; }
        Tuple(output, stride); output.WriteByte(0x89); Global(output, "collections", "OrderedDict");
        output.Write([(byte)')', (byte)'R', (byte)'t', (byte)'R']);
    }
    private static void Tuple(Stream output, long[] values)
    { output.WriteByte((byte)'('); foreach (long value in values) { Integer(output, checked((int)value)); } output.WriteByte((byte)'t'); }
    private static void Integer(Stream output, int value) { output.WriteByte((byte)'J'); output.Write(BitConverter.GetBytes(value)); }
    private static void Unicode(Stream output, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); output.WriteByte((byte)'X'); output.Write(BitConverter.GetBytes(bytes.Length)); output.Write(bytes); }
    private static void Global(Stream output, string module, string name) => output.Write(Encoding.ASCII.GetBytes($"c{module}\n{name}\n"));
    private static void Entry(ZipArchive archive, string name, byte[] bytes)
    { using Stream stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open(); stream.Write(bytes); }

    internal sealed class Source : ITensorModelSource
    {
        private readonly Dictionary<string, byte[]> _metadata;
        public string Id => "hugging-face";
        public TensorModelCandidate Candidate { get; set; }
        public string? MissingRole { get; set; }
        public List<TensorMetadataRead> Reads { get; } = [];
        public Source(SupraMetadataFixture fixture)
        {
            _metadata = new() { ["supra-model"] = fixture.TorchZip(), ["text-model"] = Safetensors(fixture.Text), ["vae-model"] = Safetensors(fixture.Vae) };
            TensorModelArtifactDefinition primary = TensorImageModels.Supra2Image.Artifacts.Single(item => item.Role == "supra-model");
            Candidate = From(primary);
        }
        private TensorModelCandidate From(TensorModelArtifactDefinition definition)
        {
            TensorArtifactProvenance provenance = definition.Provenance!;
            return new(Id, new(provenance.Repository, provenance.Revision, provenance.Path), new("SupraDiT", null, null), provenance.License, false, false,
                [new(provenance.Path, definition.Length, definition.Sha256, provenance, new($"https://huggingface.co/{provenance.Repository}/resolve/{provenance.Revision}/{provenance.Path}"))]);
        }
        public Task<TensorSourceResult<TensorModelCandidate>> GetCandidateAsync(TensorCandidateSelection selection, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selection.Repository == Candidate.Selection.Repository) { return Task.FromResult(new TensorSourceResult<TensorModelCandidate>(TensorSourceState.Available, Candidate)); }
            TensorModelArtifactDefinition match = TensorImageModels.Supra2Image.Artifacts.Single(item => item.Provenance!.Repository == selection.Repository && item.Provenance.Path == selection.Variant);
            return Task.FromResult(match.Role == MissingRole ? new TensorSourceResult<TensorModelCandidate>(TensorSourceState.NotFound)
                : new(TensorSourceState.Available, From(match)));
        }
        public Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact, TensorMetadataRead read, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads.Add(read);
            string role = artifact.Provenance.Repository == Candidate.Selection.Repository ? "supra-model" : artifact.Path.EndsWith("diffusion_pytorch_model.safetensors", StringComparison.Ordinal) ? "vae-model" : "text-model";
            byte[] metadata = _metadata[role], bytes = new byte[read.Length];
            if (read.Offset < metadata.Length)
            { metadata.AsSpan((int)read.Offset, Math.Min(read.Length, metadata.Length - (int)read.Offset)).CopyTo(bytes); }
            if (role == "supra-model" && read.Offset + read.Length == artifact.Length)
            { metadata.AsSpan(metadata.Length - 22).CopyTo(bytes.AsSpan(bytes.Length - 22)); }
            return Task.FromResult(new TensorSourceResult<TensorMetadataContent>(TensorSourceState.Available, new(bytes, read.Offset, artifact.Length!.Value)));
        }
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceModel>>> SearchAsync(TensorSourceQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceRevision>>> ListRevisionsAsync(TensorRepositoryQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceArtifact>>> ListVariantsAsync(TensorVariantQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
