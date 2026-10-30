using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM;

// Azure OpenAI uses this; Azure OpenAI is out of the build (Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Azure,
// outside the solution). It moves into that provider's own library when the provider returns to the build.
public sealed partial class LlmSettings
{
    private void AddAzureFields(List<SetupField> fields)
    {
        AzureEditorDraft azure = _azure!;
        SetupChoiceField Choice(string id, string label, string value, Action<string> set) => new(
            new(id, label) { IsRequired = true }, value, string.Empty, selection =>
            {
                if (AzureEditorDraft.Choices(id)?.Any(choice => choice.Value == selection) != true)
                    return new(SetupEditState.Invalid, [new("azure-choice-invalid", "Select an explicit Azure configuration value.", id)]);
                return Edit(() => set(selection));
            }, false);
        SetupTextField Text(string id, string label, string value, Action<string> set, bool file = false) => new(
            new(id, label) { IsRequired = true }, value, string.Empty, selection => Edit(() => set(selection)),
            pathSelection: file ? SetupPathSelectionKind.File : null);
        SetupSecretField Secret(string id, string label, bool required, bool hasValue, SetupSecretEdit change,
            Action<SetupSecretEdit> set, string help) => new(new(id, label) { IsRequired = required, HelpText = help },
                hasValue, change.Kind, selection => Edit(() => set(selection)));

        fields.Add(Choice("azure-api-mode", "API mode", azure.ApiMode.ToString(),
            selection => azure.SetMode(Enum.Parse<LlmAzureApiMode>(selection))));
        if (azure.ApiMode == LlmAzureApiMode.Versioned)
            fields.Add(Text("azure-api-version", "API version", azure.ApiVersion, value => azure.ApiVersion = value));
        fields.Add(Choice("azure-authentication", "Authentication", azure.Authentication.ToString(),
            selection => azure.SetAuthentication(Enum.Parse<LlmAzureAuthenticationMode>(selection))));
        if (azure.Authentication == LlmAzureAuthenticationMode.Entra || azure.HasEntra)
        {
            fields.Add(Choice("azure-cloud", "Cloud", azure.Cloud?.ToString() ?? string.Empty,
                value => azure.Cloud = Enum.Parse<LlmAzureCloud>(value)));
            fields.Add(Choice("azure-audience", "Token audience", azure.Audience?.ToString() ?? string.Empty,
                value => azure.Audience = Enum.Parse<LlmAzureTokenAudience>(value)));
            fields.Add(Choice("azure-identity", "Identity", azure.Identity, value => azure.Identity = value));
            if (azure.Identity is "client-secret" or "client-certificate" or "workload")
            {
                fields.Add(Text("azure-tenant-id", "Tenant ID", azure.TenantId, value => azure.TenantId = value));
                fields.Add(Text("azure-client-id", "Client ID", azure.ClientId, value => azure.ClientId = value));
            }
            if (azure.Identity == "workload")
                fields.Add(Text("azure-federated-token-file", "Federated token file", azure.TokenFile,
                    value => azure.TokenFile = value, file: true));
            if (azure.Identity == "user-managed")
            {
                fields.Add(Choice("azure-managed-id-kind", "Managed identity selector", azure.ManagedKind,
                    value => azure.ManagedKind = value));
                fields.Add(Text("azure-managed-id-value", "Managed identity ID", azure.ManagedValue,
                    value => azure.ManagedValue = value));
            }
        }
        const string protectedHelp = "Keep preserves the saved component. Replace and clear change only this draft until saved. Incompatible saved credentials require explicit clearing or replacement.";
        if (azure.Authentication == LlmAzureAuthenticationMode.ApiKey || azure.Original.ApiKey is not null || azure.ApiKeyEdit.Kind != SetupSecretEditKind.Keep)
            fields.Add(Secret("api-key", "API key", azure.Authentication == LlmAzureAuthenticationMode.ApiKey,
                !string.IsNullOrEmpty(azure.Original.ApiKey), azure.ApiKeyEdit, value => azure.ApiKeyEdit = value, protectedHelp));
        if (azure.Identity == "client-secret" && azure.Authentication == LlmAzureAuthenticationMode.Entra
            || azure.Original.Azure is LlmAzureSecret.ClientSecret || azure.ClientSecretEdit.Kind != SetupSecretEditKind.Keep)
            fields.Add(Secret("azure-client-secret", "Client secret", azure.Authentication == LlmAzureAuthenticationMode.Entra && azure.Identity == "client-secret",
                azure.Original.Azure is LlmAzureSecret.ClientSecret, azure.ClientSecretEdit, value => azure.ClientSecretEdit = value, protectedHelp));
        if (azure.Identity == "client-certificate" && azure.Authentication == LlmAzureAuthenticationMode.Entra
            || azure.Original.Azure is LlmAzureSecret.CertificatePkcs12 || azure.CertificateEdit.Kind != SetupSecretEditKind.Keep
            || azure.PasswordEdit.Kind != SetupSecretEditKind.Keep)
        {
            fields.Add(Secret("azure-certificate-pkcs12", "PKCS12 certificate (Base64)", azure.Authentication == LlmAzureAuthenticationMode.Entra && azure.Identity == "client-certificate",
                azure.Original.Azure is LlmAzureSecret.CertificatePkcs12, azure.CertificateEdit, value => azure.CertificateEdit = value,
                protectedHelp + " Clearing the certificate also removes its dependent password."));
            fields.Add(Secret("azure-certificate-password", "Certificate password", false,
                azure.Original.Azure is LlmAzureSecret.CertificatePkcs12 { Password: not null }, azure.PasswordEdit, value => azure.PasswordEdit = value,
                "Keep preserves the saved password. Clear means no password. Replace preserves the exact input, including an empty password."));
        }
    }

    // Incomplete form values cannot enter the validated public identity records.
    private sealed class AzureEditorDraft
    {
        private readonly LlmAzureConfiguration _initial;
        internal readonly LlmCredential Original;
        internal LlmAzureApiMode ApiMode;
        internal LlmAzureAuthenticationMode Authentication;
        internal bool HasEntra;
        internal LlmAzureCloud? Cloud;
        internal LlmAzureTokenAudience? Audience;
        internal string Identity = string.Empty;
        internal string TenantId = string.Empty;
        internal string ClientId = string.Empty;
        internal string TokenFile = string.Empty;
        internal string ManagedKind = string.Empty;
        internal string ManagedValue = string.Empty;
        internal bool DeploymentEdited;
        internal string Profile;
        internal string ApiVersion;
        internal SetupSecretEdit ApiKeyEdit = SetupSecretEdit.Keep;
        internal SetupSecretEdit ClientSecretEdit = SetupSecretEdit.Keep;
        internal SetupSecretEdit CertificateEdit = SetupSecretEdit.Keep;
        internal SetupSecretEdit PasswordEdit = SetupSecretEdit.Keep;

        internal AzureEditorDraft(LlmAzureConfiguration configuration, LlmCredential credential)
        {
            _initial = configuration;
            Original = credential;
            ApiMode = configuration.ApiMode;
            Authentication = configuration.AuthenticationMode;
            Profile = configuration.ModelProfileId ?? string.Empty;
            ApiVersion = configuration.ApiVersion ?? string.Empty;
            HasEntra = configuration.Entra is not null;
            Cloud = configuration.Entra?.Cloud;
            Audience = configuration.Entra?.Audience;
            switch (configuration.Entra?.Identity)
            {
                case LlmAzureIdentitySelection.ServicePrincipalSecret identity:
                    Identity = "client-secret"; TenantId = identity.TenantId; ClientId = identity.ClientId; break;
                case LlmAzureIdentitySelection.ServicePrincipalCertificate identity:
                    Identity = "client-certificate"; TenantId = identity.TenantId; ClientId = identity.ClientId; break;
                case LlmAzureIdentitySelection.WorkloadIdentity identity:
                    Identity = "workload"; TenantId = identity.TenantId; ClientId = identity.ClientId; TokenFile = identity.FederatedTokenFile; break;
                case LlmAzureIdentitySelection.SystemAssignedManagedIdentity:
                    Identity = "system-managed"; break;
                case LlmAzureIdentitySelection.UserAssignedManagedIdentity identity:
                    Identity = "user-managed";
                    (ManagedKind, ManagedValue) = identity.Id switch
                    {
                        LlmAzureManagedIdentityId.ClientId id => ("client-id", id.Value),
                        LlmAzureManagedIdentityId.ResourceId id => ("resource-id", id.Value),
                        LlmAzureManagedIdentityId.ObjectId id => ("object-id", id.Value),
                        _ => (string.Empty, string.Empty)
                    };
                    break;
            }
        }

        internal bool HasCredentialEdits => ApiKeyEdit.Kind != SetupSecretEditKind.Keep || ClientSecretEdit.Kind != SetupSecretEditKind.Keep
            || CertificateEdit.Kind != SetupSecretEditKind.Keep || PasswordEdit.Kind != SetupSecretEditKind.Keep;
        internal bool IsDirty => !TryConfiguration(_initial.DeploymentId, out LlmAzureConfiguration? configuration, []) || configuration != _initial;

        internal void SetMode(LlmAzureApiMode mode)
        {
            ApiMode = mode;
            if (mode == LlmAzureApiMode.V1) ApiVersion = string.Empty;
        }

        internal void SetAuthentication(LlmAzureAuthenticationMode mode)
        {
            Authentication = mode;
            HasEntra = mode == LlmAzureAuthenticationMode.Entra;
            if (mode == LlmAzureAuthenticationMode.ApiKey)
            {
                Cloud = null; Audience = null; Identity = string.Empty;
                TenantId = string.Empty; ClientId = string.Empty; TokenFile = string.Empty;
                ManagedKind = string.Empty; ManagedValue = string.Empty;
            }
        }

        internal static IReadOnlyList<SetupChoice>? Choices(string fieldId) => fieldId switch
        {
            "azure-api-mode" => [new("V1", "OpenAI v1"), new("Versioned", "Explicit versioned route (unavailable)")],
            "azure-authentication" => [new("ApiKey", "API key"), new("Entra", "Explicit Entra identity (unavailable)")],
            "azure-cloud" => [new("Public", "Public"), new("Government", "Government"), new("China", "China")],
            "azure-audience" => [new("Foundry", "Foundry"), new("CognitiveServices", "Cognitive Services")],
            "azure-identity" => [new("client-secret", "Service principal secret"), new("client-certificate", "Service principal certificate"),
                new("workload", "Workload identity"), new("system-managed", "System-assigned managed identity"), new("user-managed", "User-assigned managed identity")],
            "azure-managed-id-kind" => [new("client-id", "Client ID"), new("resource-id", "Resource ID"), new("object-id", "Object ID")],
            _ => null
        };

        internal SetupIssue[] Validate(string deployment)
        {
            var issues = new List<SetupIssue>();
            TryConfiguration(deployment, out _, issues);
            TryCredential(out _, issues);
            return issues.ToArray();
        }

        internal LlmAzureConfiguration Configuration(string deployment)
        {
            if (!TryConfiguration(deployment, out LlmAzureConfiguration? configuration, []))
                throw new ArgumentException("Complete the Azure configuration before using this draft.");
            return configuration!;
        }

        private bool TryConfiguration(string deployment, out LlmAzureConfiguration? configuration, List<SetupIssue> issues)
        {
            configuration = null;
            int start = issues.Count;
            void Issue(string field, string message) => issues.Add(new("azure-configuration-invalid", message, field));
            if (!Enum.IsDefined(ApiMode)) Issue("azure-api-mode", "Select an explicit API mode.");
            if (!Enum.IsDefined(Authentication)) Issue("azure-authentication", "Select an explicit authentication mode.");
            if (!AzureConfigurationData.ValidId(deployment)) Issue("model", "Enter an exact deployment ID.");
            if (!DeploymentEdited && deployment != _initial.DeploymentId)
                issues.Add(new("azure-deployment-conflict", "The selected model must match the configured Azure deployment.", "model"));
            if (!string.IsNullOrWhiteSpace(Profile) && !AzureConfigurationData.ValidId(Profile)) Issue("azure-model-profile", "Enter an exact model profile ID or clear it.");
            if (ApiMode == LlmAzureApiMode.Versioned && !AzureConfigurationData.ValidId(ApiVersion)) Issue("azure-api-version", "Enter an explicit API version. Its presence does not admit this route.");
            if (ApiMode == LlmAzureApiMode.V1 && ApiVersion.Length != 0) Issue("azure-api-mode", "Select V1 explicitly to remove incompatible API version configuration.");
            LlmAzureEntraConfiguration? entra = null;
            if (Authentication == LlmAzureAuthenticationMode.ApiKey && HasEntra)
                Issue("azure-authentication", "Select API key explicitly to remove incompatible Entra configuration.");
            if (Authentication == LlmAzureAuthenticationMode.Entra)
            {
                if (Cloud is not { } cloud || !Enum.IsDefined(cloud)) Issue("azure-cloud", "Select an explicit cloud.");
                if (Audience is not { } audience || !Enum.IsDefined(audience)) Issue("azure-audience", "Select an explicit token audience.");
                if (Identity is "client-secret" or "client-certificate" or "workload")
                {
                    if (!AzureConfigurationData.ValidId(TenantId)) Issue("azure-tenant-id", "Enter an exact tenant ID.");
                    if (!AzureConfigurationData.ValidId(ClientId)) Issue("azure-client-id", "Enter an exact client ID.");
                }
                if (Identity == "workload")
                {
                    try { _ = AzureConfigurationData.File(TokenFile); }
                    catch (ArgumentException) { Issue("azure-federated-token-file", "Enter an explicit absolute federated token file path."); }
                }
                if (Identity == "user-managed")
                {
                    if (ManagedKind is not ("client-id" or "resource-id" or "object-id")) Issue("azure-managed-id-kind", "Select an explicit managed identity selector.");
                    if (!AzureConfigurationData.ValidId(ManagedValue)) Issue("azure-managed-id-value", "Enter the exact selected managed identity ID.");
                }
                if (Identity is not ("client-secret" or "client-certificate" or "workload" or "system-managed" or "user-managed"))
                    Issue("azure-identity", "Select one of the five explicit identity families.");
                if (issues.Count == start)
                {
                    LlmAzureIdentitySelection identity = Identity switch
                    {
                        "client-secret" => new LlmAzureIdentitySelection.ServicePrincipalSecret(TenantId, ClientId),
                        "client-certificate" => new LlmAzureIdentitySelection.ServicePrincipalCertificate(TenantId, ClientId),
                        "workload" => new LlmAzureIdentitySelection.WorkloadIdentity(TenantId, ClientId, TokenFile),
                        "system-managed" => new LlmAzureIdentitySelection.SystemAssignedManagedIdentity(),
                        _ => new LlmAzureIdentitySelection.UserAssignedManagedIdentity(ManagedKind switch
                        {
                            "client-id" => new LlmAzureManagedIdentityId.ClientId(ManagedValue),
                            "resource-id" => new LlmAzureManagedIdentityId.ResourceId(ManagedValue),
                            _ => new LlmAzureManagedIdentityId.ObjectId(ManagedValue)
                        })
                    };
                    entra = new(Cloud!.Value, Audience!.Value, identity);
                }
            }
            if (issues.Count != start) return false;
            configuration = new()
            {
                ApiMode = ApiMode,
                AuthenticationMode = Authentication,
                DeploymentId = deployment,
                ModelProfileId = string.IsNullOrWhiteSpace(Profile) ? null : Profile,
                ApiVersion = ApiMode == LlmAzureApiMode.Versioned ? ApiVersion : null,
                Entra = entra
            };
            return true;
        }

        private bool TryCredential(out LlmCredential credential, List<SetupIssue> issues)
        {
            int start = issues.Count;
            void Issue(string field, string message) => issues.Add(new("azure-credential-invalid", message, field));
            string? apiKey = ApiKeyEdit.Kind switch { SetupSecretEditKind.Replace => ApiKeyEdit.Value, SetupSecretEditKind.Clear => null, _ => Original.ApiKey };
            LlmAzureSecret? secret = Original.Azure;
            if (ClientSecretEdit.Kind == SetupSecretEditKind.Clear && secret is LlmAzureSecret.ClientSecret
                || CertificateEdit.Kind == SetupSecretEditKind.Clear && secret is LlmAzureSecret.CertificatePkcs12) secret = null;
            if (ClientSecretEdit.Kind == SetupSecretEditKind.Replace && CertificateEdit.Kind == SetupSecretEditKind.Replace)
                Issue("azure-identity", "Replace only the protected payload for the selected identity.");
            if (ClientSecretEdit.Kind == SetupSecretEditKind.Replace)
                secret = new LlmAzureSecret.ClientSecret(ClientSecretEdit.Value!);
            if (CertificateEdit.Kind == SetupSecretEditKind.Replace)
                secret = new LlmAzureSecret.CertificatePkcs12(CertificateEdit.Value!, (Original.Azure as LlmAzureSecret.CertificatePkcs12)?.Password);
            if (PasswordEdit.Kind != SetupSecretEditKind.Keep)
            {
                if (CertificateEdit.Kind == SetupSecretEditKind.Clear)
                {
                    if (PasswordEdit.Kind == SetupSecretEditKind.Replace) Issue("azure-certificate-password", "A password replacement requires a retained or replaced certificate.");
                }
                else if (secret is LlmAzureSecret.CertificatePkcs12 certificate)
                    secret = certificate with { Password = PasswordEdit.Kind == SetupSecretEditKind.Clear ? null : PasswordEdit.Value };
                else if (PasswordEdit.Kind == SetupSecretEditKind.Replace)
                    Issue("azure-certificate-password", "A password replacement requires a retained or replaced certificate.");
            }
            credential = Original with { ApiKey = apiKey, Azure = secret };
            if (Authentication == LlmAzureAuthenticationMode.ApiKey)
            {
                if (string.IsNullOrWhiteSpace(apiKey)) Issue("api-key", "Supply a protected API key.");
                if (secret is not null) Issue("azure-authentication", "Explicitly clear the incompatible protected Azure identity payload.");
            }
            if (Authentication == LlmAzureAuthenticationMode.Entra)
            {
                if (apiKey is not null) Issue("api-key", "Explicitly clear the incompatible API key.");
                if (Identity == "client-secret" && (secret is not LlmAzureSecret.ClientSecret clientSecret || string.IsNullOrWhiteSpace(clientSecret.Value)))
                    Issue("azure-client-secret", "Supply the matching protected client secret.");
                if (Identity == "client-certificate" && (secret is not LlmAzureSecret.CertificatePkcs12 certificate || string.IsNullOrWhiteSpace(certificate.Base64)))
                    Issue("azure-certificate-pkcs12", "Supply the matching protected PKCS12 payload.");
                if (Identity is "workload" or "system-managed" or "user-managed" && secret is not null)
                    Issue("azure-identity", "Explicitly clear the protected Azure payload for this identity.");
            }
            if (Original.OAuth is not null || Original.Aws is not null || Original.Google is not null || Original.Copilot is not null || Original.CopilotRuntime is not null)
                Issue("azure-authentication", "This Azure configuration cannot use other protected credential families.");
            return issues.Count == start;
        }

        internal LlmCredential ComposeCredential()
        {
            if (!TryCredential(out LlmCredential credential, [])) throw new ArgumentException("Complete the matching Azure credential edits before using this draft.");
            return credential;
        }

        internal SetupSecretEdit CredentialChange()
        {
            if (!HasCredentialEdits) return SetupSecretEdit.Keep;
            LlmCredential credential = ComposeCredential();
            return credential.ApiKey is null && credential.Azure is null && credential.OAuth is null && credential.Aws is null
                && credential.Google is null && credential.Copilot is null && credential.CopilotRuntime is null && credential.SecretHeaders.Count == 0
                ? SetupSecretEdit.Clear : SetupSecretEdit.Replace(credential.Serialize());
        }
    }
}
