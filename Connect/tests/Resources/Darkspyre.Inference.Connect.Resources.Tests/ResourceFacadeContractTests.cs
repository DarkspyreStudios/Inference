using System.Reflection;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Resources.Tests;

public sealed class ResourceFacadeContractTests
{
    private static readonly (string Name, Type Result, Type[] Parameters)[] Operations =
    [
        ("CreateAsync", typeof(Task<LlmBatch>), [typeof(string), typeof(string), typeof(CancellationToken)]),
        ("AddRequestsAsync", typeof(Task), [typeof(string), typeof(LlmBatch), typeof(IReadOnlyList<LlmBatchRequest>),
            typeof(IInferenceResourceAccess), typeof(CancellationToken)]),
        ("GetAsync", typeof(Task<LlmBatch>), [typeof(string), typeof(LlmBatch), typeof(CancellationToken)]),
        ("ListAsync", typeof(Task<LlmBatchPage>), [typeof(string), typeof(LlmResourcePageRequest), typeof(CancellationToken)]),
        ("ListRequestsAsync", typeof(Task<LlmBatchRequestPage>), [typeof(string), typeof(LlmBatch),
            typeof(LlmResourcePageRequest), typeof(CancellationToken)]),
        ("GetResultsAsync", typeof(Task<LlmBatchResultPage>), [typeof(string), typeof(LlmBatch),
            typeof(LlmResourcePageRequest), typeof(LlmMediaMaterializationOptions),
            typeof(IInferenceResourceAccess), typeof(CancellationToken)]),
        ("CancelAsync", typeof(Task<LlmBatch>), [typeof(string), typeof(LlmBatch), typeof(CancellationToken)]),
        ("SubmitDeferredAsync", typeof(Task<LlmDeferredCompletion>), [typeof(string), typeof(LlmChatRequest),
            typeof(IInferenceResourceAccess), typeof(CancellationToken)]),
        ("GetDeferredAsync", typeof(Task<LlmDeferredResult>), [typeof(string), typeof(LlmDeferredCompletion),
            typeof(IInferenceResourceAccess), typeof(CancellationToken)]),
        ("CreateFromRequestsAsync", typeof(Task<LlmBatch>), [typeof(string), typeof(string),
            typeof(IReadOnlyList<LlmBatchRequest>), typeof(IInferenceResourceAccess), typeof(CancellationToken)])
    ];

    [Fact]
    public void BatchInterfaceExposesAllTenExactOperations()
    {
        Assert.Equal(Operations.Length, typeof(ILlmBatchClient).GetMethods().Length);
        AssertOperations(typeof(ILlmBatchClient));
    }

    [Fact]
    public void ResourcesAssemblyExposesTheCallableFacade()
    {
        Type? facade = typeof(ILlmBatchClient).Assembly.GetType(
            "Darkspyre.Inference.Connect.Resources.LlmResourceProviderService");
        Assert.NotNull(facade);
        Assert.True(facade.IsPublic);
        Assert.False(facade.IsAbstract);
        Assert.True(typeof(ILlmBatchClient).IsAssignableFrom(facade));
        AssertOperations(facade);
    }

    private static void AssertOperations(Type owner)
    {
        var nullability = new NullabilityInfoContext();
        foreach ((string name, Type result, Type[] types) in Operations)
        {
            MethodInfo? method = owner.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, types);
            Assert.NotNull(method);
            Assert.Equal(result, method.ReturnType);
            ParameterInfo[] parameters = method.GetParameters();
            Assert.Equal("connectionId", parameters[0].Name);
            ParameterInfo cancellation = parameters[^1];
            Assert.Equal("cancellationToken", cancellation.Name);
            Assert.True(cancellation.IsOptional);
            Assert.Null(cancellation.DefaultValue);
            foreach (ParameterInfo parameter in parameters[..^1])
            {
                bool isResources = parameter.ParameterType == typeof(IInferenceResourceAccess);
                Assert.Equal(isResources, parameter.IsOptional);
                Assert.Equal(isResources ? NullabilityState.Nullable : NullabilityState.NotNull,
                    nullability.Create(parameter).ReadState);
                if (isResources)
                {
                    Assert.Equal("resources", parameter.Name);
                    Assert.Null(parameter.DefaultValue);
                }
            }
        }
    }
}
