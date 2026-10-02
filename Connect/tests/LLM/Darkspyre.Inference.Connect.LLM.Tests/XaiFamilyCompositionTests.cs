using System.Reflection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFamilyCompositionTests
{
    [Fact]
    public void OptionalFacadeInputPreservesExplicitTwoArgumentComposition()
    {
        ConstructorInfo constructor = Constructor();
        Assert.True(constructor.GetParameters()[2].IsOptional);
        Assert.Equal("providerService", constructor.GetParameters()[2].Name);
        Assert.Null(constructor.GetParameters()[2].DefaultValue);
        var source = new Source();
        using var access = new LlmConnectionAccess(source, []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        Assert.Equal("selected", binding.Selection.ConnectionId);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public void BorrowedFacadeRequiresTheExactAccessOwnerBeforeEffects()
    {
        ConstructorInfo constructor = Constructor();
        var source = new Source();
        using var first = new LlmConnectionAccess(source, []);
        using var second = new LlmConnectionAccess(source, []);
        using var facade = new LlmProviderService(source, [], [], connectionAccess: first);
        var error = Assert.Throws<TargetInvocationException>(() => constructor.Invoke([new XaiConnectionSelection("selected"), second, facade]));
        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.IsType<XaiFamilyBinding>(constructor.Invoke([new XaiConnectionSelection("selected"), first, facade]));
        Assert.Equal(0, source.Calls);
    }

    private static ConstructorInfo Constructor()
    {
        ConstructorInfo? constructor = typeof(XaiFamilyBinding).GetConstructor([typeof(XaiConnectionSelection), typeof(LlmConnectionAccess), typeof(LlmProviderService)]);
        Assert.NotNull(constructor);
        return constructor;
    }

    private sealed class Source : ILlmConnectionSource
    {
        internal int Calls;
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("No resolution permitted."); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No refresh permitted.");
    }
}
