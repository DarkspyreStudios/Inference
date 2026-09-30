namespace GpuArbiter;

public sealed class ArbiterOptions
{
    public string LlmUrl { get; set; } = "http://0.0.0.0:8080";
    public string ComfyProxyUrl { get; set; } = "http://0.0.0.0:8189";
    public int KeepaliveSeconds { get; set; } = 5;
    public string LogDir { get; set; } = @"G:\AI\gpu-arbiter\logs";
    public LlamaOptions Llama { get; set; } = new();
    public ComfyOptions Comfy { get; set; } = new();
    public VramOptions Vram { get; set; } = new();

    public void Validate()
    {
        var llm = new Uri(LlmUrl);
        var comfy = new Uri(ComfyProxyUrl);
        if (llm.Scheme != "http" || comfy.Scheme != "http" || llm.Port == comfy.Port ||
            Llama.Port is < 1 or > 65535 || Llama.Port == llm.Port || Llama.Port == comfy.Port ||
            KeepaliveSeconds <= 0 || Comfy.PollSeconds <= 0 || Llama.LoadTimeoutSeconds <= 0 ||
            Vram.ReleaseTimeoutSeconds <= 0 || Vram.ReleasedMaxUsedMiB < 0)
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
public sealed class ComfyOptions
{
    public string Url { get; set; } = "http://127.0.0.1:8188";
    public double PollSeconds { get; set; } = 1;
}
public sealed class VramOptions
{
    public int ReleasedMaxUsedMiB { get; set; } = 2500;
    public double ReleaseTimeoutSeconds { get; set; } = 60;
}
