namespace GpuArbiter;

public sealed class ArbiterOptions : CoordinatorOptions
{
    public string LlmUrl { get; set; } = "http://0.0.0.0:8080";
    public string ComfyProxyUrl { get; set; } = "http://0.0.0.0:8189";
    public int KeepaliveSeconds { get; set; } = 5;
    public string LogDir { get; set; } = @"G:\AI\gpu-arbiter\logs";
    public LlamaOptions Llama { get; set; } = new();
    public override int ModelLoadTimeoutSeconds { get => Llama.LoadTimeoutSeconds; set => Llama.LoadTimeoutSeconds = value; }

    public void Validate()
    {
        var llm = new Uri(LlmUrl);
        var comfy = new Uri(ComfyProxyUrl);
        if (llm.Scheme != "http" || comfy.Scheme != "http" || llm.Port == comfy.Port ||
            Llama.Port is < 1 or > 65535 || Llama.Port == llm.Port || Llama.Port == comfy.Port ||
            KeepaliveSeconds <= 0 || Comfy.PollSeconds <= 0 || Llama.LoadTimeoutSeconds <= 0 ||
            ReleaseTimeoutSeconds <= 0 || Vram.ReleasedMaxUsedMiB < 0)
            throw new InvalidOperationException("Invalid arbiter ports or timing configuration.");
        if (!Uri.TryCreate(Comfy.Url, UriKind.Absolute, out var backend) || backend.Scheme != "http" || !backend.IsLoopback)
            throw new InvalidOperationException("Comfy.Url must be a loopback HTTP address.");
        if (!Path.IsPathFullyQualified(Llama.Exe) || !Path.IsPathFullyQualified(Llama.Preset) || !Path.IsPathFullyQualified(LogDir))
            throw new InvalidOperationException("Executable, preset and log paths must be absolute.");
    }
}
public sealed class LlamaOptions
{
    public string Exe { get; set; } = @"G:\AI\llama.cpp\b11238\llama-server.exe";
    public string Preset { get; set; } = @"G:\AI\models\presets.ini";
    public int Port { get; set; } = 8081;
    public int LoadTimeoutSeconds { get; set; } = 300;
    public string Url => $"http://127.0.0.1:{Port}";
}
