using System.Text.Json.Serialization;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The Azure resource API selected by this connection.</summary>
public enum LlmAzureApiMode
{
    /// <summary>The resource's OpenAI v1 API.</summary>
    V1 = 0,

    /// <summary>An explicit dated route. Its declaration does not admit an unimplemented route.</summary>
    Versioned = 1
}

/// <summary>The explicitly selected Azure authentication scheme.</summary>
public enum LlmAzureAuthenticationMode
{
    /// <summary>A protected resource API key, sent only in the api-key header.</summary>
    ApiKey = 0,

    /// <summary>One explicitly selected identity, without an ambient credential chain.</summary>
    Entra = 1
}

public enum LlmAzureCloud { Public = 0, Government = 1, China = 2 }
public enum LlmAzureTokenAudience { Foundry = 0, CognitiveServices = 1 }

/// <summary>Azure-owned routing and model evidence. The endpoint supplies only the HTTPS resource authority.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAzureConfiguration
{
    public LlmAzureApiMode ApiMode { get; init; } = LlmAzureApiMode.V1;
    public LlmAzureAuthenticationMode AuthenticationMode { get; init; } = LlmAzureAuthenticationMode.ApiKey;

    /// <summary>The deployment name used as the wire model. Requests must select this same deployment.</summary>
    public string DeploymentId { get; init; } = string.Empty;

    /// <summary>An optional underlying model profile. This is not a deployment name or access guarantee.</summary>
    public string? ModelProfileId { get; init; }

    public string? ApiVersion { get; init; }
    public LlmAzureEntraConfiguration? Entra { get; init; }
    public override string ToString() => "LlmAzureConfiguration { <configuration> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAzureEntraConfiguration(LlmAzureCloud Cloud, LlmAzureTokenAudience Audience,
    LlmAzureIdentitySelection Identity)
{
    [JsonRequired] public LlmAzureCloud Cloud { get; init => field = RuntimeAuthData.Defined(value); } = RuntimeAuthData.Defined(Cloud);
    [JsonRequired] public LlmAzureTokenAudience Audience { get; init => field = RuntimeAuthData.Defined(value); } = RuntimeAuthData.Defined(Audience);
    [JsonRequired] public LlmAzureIdentitySelection Identity { get; init => field = CallerData.Required(value); } = CallerData.Required(Identity);
    public override string ToString() => "LlmAzureEntraConfiguration { <configuration> }";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(LlmAzureIdentitySelection.ServicePrincipalSecret), "client-secret")]
[JsonDerivedType(typeof(LlmAzureIdentitySelection.ServicePrincipalCertificate), "client-certificate")]
[JsonDerivedType(typeof(LlmAzureIdentitySelection.WorkloadIdentity), "workload")]
[JsonDerivedType(typeof(LlmAzureIdentitySelection.SystemAssignedManagedIdentity), "system-managed")]
[JsonDerivedType(typeof(LlmAzureIdentitySelection.UserAssignedManagedIdentity), "user-managed")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAzureIdentitySelection
{
    private protected LlmAzureIdentitySelection() { }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ServicePrincipalSecret(string TenantId, string ClientId) : LlmAzureIdentitySelection
    {
        [JsonRequired] public string TenantId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(TenantId);
        [JsonRequired] public string ClientId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(ClientId);
        public override string ToString() => "ServicePrincipalSecret { <configuration> }";
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ServicePrincipalCertificate(string TenantId, string ClientId) : LlmAzureIdentitySelection
    {
        [JsonRequired] public string TenantId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(TenantId);
        [JsonRequired] public string ClientId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(ClientId);
        public override string ToString() => "ServicePrincipalCertificate { <configuration> }";
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record WorkloadIdentity(string TenantId, string ClientId, string FederatedTokenFile) : LlmAzureIdentitySelection
    {
        [JsonRequired] public string TenantId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(TenantId);
        [JsonRequired] public string ClientId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(ClientId);
        [JsonRequired] public string FederatedTokenFile { get; init => field = AzureConfigurationData.File(value); } = AzureConfigurationData.File(FederatedTokenFile);
        public override string ToString() => "WorkloadIdentity { <configuration> }";
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record SystemAssignedManagedIdentity : LlmAzureIdentitySelection
    {
        public override string ToString() => "SystemAssignedManagedIdentity { <configuration> }";
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record UserAssignedManagedIdentity(LlmAzureManagedIdentityId Id) : LlmAzureIdentitySelection
    {
        [JsonRequired] public LlmAzureManagedIdentityId Id { get; init => field = CallerData.Required(value); } = CallerData.Required(Id);
        public override string ToString() => "UserAssignedManagedIdentity { <configuration> }";
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(LlmAzureManagedIdentityId.ClientId), "client-id")]
[JsonDerivedType(typeof(LlmAzureManagedIdentityId.ResourceId), "resource-id")]
[JsonDerivedType(typeof(LlmAzureManagedIdentityId.ObjectId), "object-id")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAzureManagedIdentityId
{
    private protected LlmAzureManagedIdentityId() { }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ClientId(string Value) : LlmAzureManagedIdentityId
    {
        [JsonRequired] public string Value { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(Value);
        public override string ToString() => "ClientId { <configuration> }";
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ResourceId(string Value) : LlmAzureManagedIdentityId
    {
        [JsonRequired] public string Value { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(Value);
        public override string ToString() => "ResourceId { <configuration> }";
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ObjectId(string Value) : LlmAzureManagedIdentityId
    {
        [JsonRequired] public string Value { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(Value);
        public override string ToString() => "ObjectId { <configuration> }";
    }
}

/// <summary>Protected-store data, never a token cache or an authentication observation.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(LlmAzureSecret.ClientSecret), "client-secret")]
[JsonDerivedType(typeof(LlmAzureSecret.CertificatePkcs12), "certificate-pkcs12")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAzureSecret
{
    private protected LlmAzureSecret() { }
    public override string ToString() => "LlmAzureSecret { [redacted] }";

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ClientSecret(string Value) : LlmAzureSecret
    {
        [JsonRequired] public string Value { get; init => field = CallerData.Required(value); } = CallerData.Required(Value);
        public override string ToString() => "LlmAzureSecret { [redacted] }";
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record CertificatePkcs12(string Base64, string? Password = null) : LlmAzureSecret
    {
        [JsonRequired] public string Base64 { get; init => field = CallerData.Required(value); } = CallerData.Required(Base64);
        public override string ToString() => "LlmAzureSecret { [redacted] }";
    }
}

internal static class AzureConfigurationData
{
    internal static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && !value.Any(char.IsControl);
    internal static string Id(string value) => ValidId(value) ? value
        : throw new ArgumentException("An explicit trimmed, control-free Azure identifier is required.", nameof(value));
    internal static string File(string value) => CallerData.Path(Id(value));
}
