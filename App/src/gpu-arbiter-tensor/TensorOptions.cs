using Darkspyre.Inference.Tensor.Common;

namespace GpuArbiter.Tensor;

public sealed class TensorOptions : CoordinatorOptions
{
    public string InferenceUrl { get; set; } = "http://0.0.0.0:8082";
    public string ComfyProxyUrl { get; set; } = "http://0.0.0.0:8190";
    public string Store { get; set; } = @"G:\AI\Tensor";
    public string LogDir { get; set; } = @"G:\AI\gpu-arbiter-tensor\logs";
    public int KeepaliveSeconds { get; set; } = 5;
    public int MaximumChoices { get; set; } = 8;
    public long MaximumRequestBytes { get; set; } = 25 * 1024 * 1024;
    public List<TensorModelOptions> Models { get; set; } = [];

    public void Validate()
    {
        if (!Uri.TryCreate(InferenceUrl, UriKind.Absolute, out var inference) || inference.Scheme != "http" ||
            !Uri.TryCreate(ComfyProxyUrl, UriKind.Absolute, out var proxy) || proxy.Scheme != "http" ||
            inference.Port == proxy.Port || !Uri.TryCreate(Comfy.Url, UriKind.Absolute, out var comfy) ||
            comfy.Scheme != "http" || !comfy.IsLoopback ||
            !Path.IsPathFullyQualified(Store) || !Path.IsPathFullyQualified(LogDir) ||
            KeepaliveSeconds <= 0 || MaximumChoices <= 0 || MaximumRequestBytes <= 0 || MaximumRequestBytes > 128 * 1024 * 1024 ||
            ModelLoadTimeoutSeconds <= 0 || Comfy.PollSeconds <= 0 ||
            ReleaseTimeoutSeconds <= 0 || Vram.ReleasedMaxUsedMiB < 0)
            throw new InvalidOperationException("Invalid Tensor arbiter paths, ports or limits.");
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in Models)
        {
            if (string.IsNullOrWhiteSpace(model.Alias) || model.Alias.Length > 256 || model.Alias.Any(char.IsControl) ||
                !aliases.Add(model.Alias) || string.IsNullOrWhiteSpace(model.ImplementationId) ||
                !Path.IsPathFullyQualified(model.Manifest) || !Enum.IsDefined(model.Backend))
                throw new InvalidOperationException("Invalid or duplicate Tensor model configuration.");
        }
    }
}

public sealed class TensorModelOptions
{
    public string Alias { get; set; } = string.Empty;
    public string ImplementationId { get; set; } = string.Empty;
    public string Manifest { get; set; } = string.Empty;
    public TensorBackendKind Backend { get; set; } = TensorBackendKind.Auto;
}
