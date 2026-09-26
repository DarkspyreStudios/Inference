using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Tensor.ConsoleTester;

/// <summary>
/// Runs an agent turn with a host tool through Connect's SelfHost transport, over the engine this tester
/// loaded. <see cref="TesterHost"/> is the smallest <see cref="ILlmSelfHost"/>: one engine, its
/// capabilities captured once at load.
/// </summary>
internal static class SelfHostSuite
{
    private const string EngineId = "tester";

    public static async Task<bool> RunAsync(ILlmInference engine, string modelId, CancellationToken cancellationToken)
    {
        LlmCapabilities capabilities = await engine.GetCapabilitiesAsync(modelId, cancellationToken);
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(new TesterConnections());
        services.AddSingleton<ILlmSelfHost>(new TesterHost(engine, capabilities));
        services.AddLlmConnect();
        services.AddLlmConnectSelfHost();
        await using ServiceProvider provider = services.BuildServiceProvider();
        LlmAgentProviderService agents = provider.GetRequiredService<LlmAgentProviderService>();

        Console.WriteLine();
        Console.WriteLine("== selfhost: agent turn with a host tool");
        var ran = new List<string>();
        await using ILlmAgentSession session = await agents.StartSessionAsync(EngineId, new LlmAgentSessionOptions
        {
            ModelId = modelId,
            Tools = [new() { Name = "get_time", Description = "Returns the current local date and time.",
                InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }) }],
            ToolHandler = (call, _) =>
            {
                string now = DateTime.Now.ToString("F", CultureInfo.InvariantCulture);
                ran.Add(call.Name);
                Console.WriteLine();
                Console.WriteLine($"  -> ran {call.Name} = {now}");
                return Task.FromResult(LlmToolOutput.FromText(now));
            }
        }, cancellationToken);

        LlmAgentTurnCompletedEvent? end = null;
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("What time is it right now? Use the get_time tool.")] },
            cancellationToken))
        {
            switch (item)
            {
                case LlmAgentTextDeltaEvent text: Console.Write(text.Delta); break;
                case LlmAgentTurnCompletedEvent completed: end = completed; break;
            }
        }

        Console.WriteLine();
        bool passed = ran.Count > 0 && end?.Status == LlmAgentTurnStatus.Completed;
        Console.WriteLine(passed ? $"  Completed; the agent ran {string.Join(", ", ran)}."
            : $"  FAILED: {end?.Reason} {end?.Error}; tools run: {ran.Count}.");
        return passed;
    }

    private sealed class TesterHost(ILlmInference engine, LlmCapabilities capabilities) : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmSelfHostEngine>>([new(EngineId, "Tester engine")]);

        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            engineId == EngineId ? Task.FromResult(engine)
                : throw new InferenceException(InferenceFailureCode.ModelUnavailable, $"Engine '{engineId}' is not loaded.");

        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => engineId == EngineId ? capabilities : null;
    }

    private sealed class TesterConnections : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new() { Version = "1", Transport = LlmTransport.SelfHost, Endpoint = EngineId });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A SelfHost connection has no credential.");
    }
}
