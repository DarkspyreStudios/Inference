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

/// <summary>The Azure cloud that issues Entra tokens.</summary>
public enum LlmAzureCloud
{
    /// <summary>The Azure public cloud.</summary>
    Public = 0,

    /// <summary>Azure Government.</summary>
    Government = 1,

    /// <summary>Azure operated in China.</summary>
    China = 2
}

/// <summary>The resource an Entra token is issued for.</summary>
public enum LlmAzureTokenAudience
{
    /// <summary>Azure AI Foundry.</summary>
    Foundry = 0,

    /// <summary>Azure Cognitive Services.</summary>
    CognitiveServices = 1
}

/// <summary>Azure-owned routing and model evidence. The endpoint supplies only the HTTPS resource authority.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAzureConfiguration
{
    /// <summary>The Azure resource API the connection calls.</summary>
    public LlmAzureApiMode ApiMode { get; init; } = LlmAzureApiMode.V1;
    /// <summary>The authentication scheme the connection uses.</summary>
    public LlmAzureAuthenticationMode AuthenticationMode { get; init; } = LlmAzureAuthenticationMode.ApiKey;

    /// <summary>The deployment name used as the wire model. Requests must select this same deployment.</summary>
    public string DeploymentId { get; init; } = string.Empty;

    /// <summary>An optional underlying model profile. This is not a deployment name or access guarantee.</summary>
    public string? ModelProfileId { get; init; }

    /// <summary>The dated API version for a versioned route, when one is selected.</summary>
    public string? ApiVersion { get; init; }
    /// <summary>The Entra identity settings, when the connection authenticates with Entra.</summary>
    public LlmAzureEntraConfiguration? Entra { get; init; }
    /// <summary>Returns a placeholder that hides the configuration values.</summary>
    public override string ToString() => "LlmAzureConfiguration { <configuration> }";
}

/// <summary>The Entra cloud, token audience and identity used to obtain tokens.</summary>
/// <param name="Cloud">The Azure cloud that issues tokens.</param>
/// <param name="Audience">The resource the tokens are issued for.</param>
/// <param name="Identity">The identity that requests tokens.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAzureEntraConfiguration(LlmAzureCloud Cloud, LlmAzureTokenAudience Audience,
    LlmAzureIdentitySelection Identity)
{
    /// <summary>The Azure cloud that issues tokens. Throws when the value is undefined.</summary>
    [JsonRequired] public LlmAzureCloud Cloud { get; init => field = RuntimeAuthData.Defined(value); } = RuntimeAuthData.Defined(Cloud);
    /// <summary>The resource the tokens are issued for. Throws when the value is undefined.</summary>
    [JsonRequired] public LlmAzureTokenAudience Audience { get; init => field = RuntimeAuthData.Defined(value); } = RuntimeAuthData.Defined(Audience);
    /// <summary>The identity that requests tokens.</summary>
    [JsonRequired] public LlmAzureIdentitySelection Identity { get; init => field = CallerData.Required(value); } = CallerData.Required(Identity);
    /// <summary>Returns a placeholder that hides the configuration values.</summary>
    public override string ToString() => "LlmAzureEntraConfiguration { <configuration> }";
}

/// <summary>The single Entra identity a connection authenticates as.</summary>
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

    /// <summary>A service principal that authenticates with a client secret.</summary>
    /// <param name="TenantId">The Entra tenant identifier.</param>
    /// <param name="ClientId">The application client identifier.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ServicePrincipalSecret(string TenantId, string ClientId) : LlmAzureIdentitySelection
    {
        /// <summary>The Entra tenant identifier. Throws when it is blank, untrimmed or contains control characters.</summary>
        [JsonRequired] public string TenantId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(TenantId);
        /// <summary>The application client identifier. Throws when it is blank, untrimmed or contains control characters.</summary>
        [JsonRequired] public string ClientId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(ClientId);
        /// <summary>Returns a placeholder that hides the configuration values.</summary>
        public override string ToString() => "ServicePrincipalSecret { <configuration> }";
    }

    /// <summary>A service principal that authenticates with a certificate.</summary>
    /// <param name="TenantId">The Entra tenant identifier.</param>
    /// <param name="ClientId">The application client identifier.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ServicePrincipalCertificate(string TenantId, string ClientId) : LlmAzureIdentitySelection
    {
        /// <summary>The Entra tenant identifier. Throws when it is blank, untrimmed or contains control characters.</summary>
        [JsonRequired] public string TenantId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(TenantId);
        /// <summary>The application client identifier. Throws when it is blank, untrimmed or contains control characters.</summary>
        [JsonRequired] public string ClientId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(ClientId);
        /// <summary>Returns a placeholder that hides the configuration values.</summary>
        public override string ToString() => "ServicePrincipalCertificate { <configuration> }";
    }

    /// <summary>A workload identity that exchanges a federated token file for Entra tokens.</summary>
    /// <param name="TenantId">The Entra tenant identifier.</param>
    /// <param name="ClientId">The application client identifier.</param>
    /// <param name="FederatedTokenFile">The path of the federated token file.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record WorkloadIdentity(string TenantId, string ClientId, string FederatedTokenFile) : LlmAzureIdentitySelection
    {
        /// <summary>The Entra tenant identifier. Throws when it is blank, untrimmed or contains control characters.</summary>
        [JsonRequired] public string TenantId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(TenantId);
        /// <summary>The application client identifier. Throws when it is blank, untrimmed or contains control characters.</summary>
        [JsonRequired] public string ClientId { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(ClientId);
        /// <summary>The path of the federated token file.</summary>
        [JsonRequired] public string FederatedTokenFile { get; init => field = AzureConfigurationData.File(value); } = AzureConfigurationData.File(FederatedTokenFile);
        /// <summary>Returns a placeholder that hides the configuration values.</summary>
        public override string ToString() => "WorkloadIdentity { <configuration> }";
    }

    /// <summary>The system-assigned managed identity of the host.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record SystemAssignedManagedIdentity : LlmAzureIdentitySelection
    {
        /// <summary>Returns a placeholder that hides the configuration values.</summary>
        public override string ToString() => "SystemAssignedManagedIdentity { <configuration> }";
    }

    /// <summary>A user-assigned managed identity.</summary>
    /// <param name="Id">The identifier that selects the managed identity.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record UserAssignedManagedIdentity(LlmAzureManagedIdentityId Id) : LlmAzureIdentitySelection
    {
        /// <summary>The identifier that selects the managed identity.</summary>
        [JsonRequired] public LlmAzureManagedIdentityId Id { get; init => field = CallerData.Required(value); } = CallerData.Required(Id);
        /// <summary>Returns a placeholder that hides the configuration values.</summary>
        public override string ToString() => "UserAssignedManagedIdentity { <configuration> }";
    }
}

/// <summary>The identifier that selects a user-assigned managed identity.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(LlmAzureManagedIdentityId.ClientId), "client-id")]
[JsonDerivedType(typeof(LlmAzureManagedIdentityId.ResourceId), "resource-id")]
[JsonDerivedType(typeof(LlmAzureManagedIdentityId.ObjectId), "object-id")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAzureManagedIdentityId
{
    private protected LlmAzureManagedIdentityId() { }

    /// <summary>Selects the managed identity by its client identifier.</summary>
    /// <param name="Value">The client identifier.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ClientId(string Value) : LlmAzureManagedIdentityId
    {
        /// <summary>The client identifier. Throws when it is blank, untrimmed or contains control characters.</summary>
        [JsonRequired] public string Value { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(Value);
        /// <summary>Returns a placeholder that hides the identifier.</summary>
        public override string ToString() => "ClientId { <configuration> }";
    }

    /// <summary>Selects the managed identity by its Azure resource identifier.</summary>
    /// <param name="Value">The Azure resource identifier.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ResourceId(string Value) : LlmAzureManagedIdentityId
    {
        /// <summary>The Azure resource identifier. Throws when it is blank, untrimmed or contains control characters.</summary>
        [JsonRequired] public string Value { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(Value);
        /// <summary>Returns a placeholder that hides the identifier.</summary>
        public override string ToString() => "ResourceId { <configuration> }";
    }

    /// <summary>Selects the managed identity by its object identifier.</summary>
    /// <param name="Value">The object identifier.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ObjectId(string Value) : LlmAzureManagedIdentityId
    {
        /// <summary>The object identifier. Throws when it is blank, untrimmed or contains control characters.</summary>
        [JsonRequired] public string Value { get; init => field = AzureConfigurationData.Id(value); } = AzureConfigurationData.Id(Value);
        /// <summary>Returns a placeholder that hides the identifier.</summary>
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
    /// <summary>Returns a placeholder that hides the secret.</summary>
    public override string ToString() => "LlmAzureSecret { [redacted] }";

    /// <summary>A service principal client secret.</summary>
    /// <param name="Value">The client secret.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ClientSecret(string Value) : LlmAzureSecret
    {
        /// <summary>The client secret.</summary>
        [JsonRequired] public string Value { get; init => field = CallerData.Required(value); } = CallerData.Required(Value);
        /// <summary>Returns a placeholder that hides the secret.</summary>
        public override string ToString() => "LlmAzureSecret { [redacted] }";
    }

    /// <summary>A service principal certificate in PKCS #12 form.</summary>
    /// <param name="Base64">The base64-encoded PKCS #12 data.</param>
    /// <param name="Password">The password of the PKCS #12 data, when it has one.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record CertificatePkcs12(string Base64, string? Password = null) : LlmAzureSecret
    {
        /// <summary>The base64-encoded PKCS #12 data.</summary>
        [JsonRequired] public string Base64 { get; init => field = CallerData.Required(value); } = CallerData.Required(Base64);
        /// <summary>Returns a placeholder that hides the secret.</summary>
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
