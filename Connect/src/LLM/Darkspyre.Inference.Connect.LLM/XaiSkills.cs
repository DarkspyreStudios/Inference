using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record XaiSkill(string Id, [property: JsonRequired] DateTimeOffset CreatedAt,
    string Name, string Description, string DefaultVersion, string LatestVersion)
{
    [JsonRequired] public string Id { get; init => field = FileContractData.Identity(value); } = FileContractData.Identity(Id);
    [JsonRequired] public string Name { get; init => field = FileContractData.Required(value); } = FileContractData.Required(Name);
    [JsonRequired] public string Description { get; init => field = FileContractData.Required(value); } = FileContractData.Required(Description);
    [JsonRequired] public string DefaultVersion { get; init => field = FileContractData.Required(value); } = FileContractData.Required(DefaultVersion);
    [JsonRequired] public string LatestVersion { get; init => field = FileContractData.Required(value); } = FileContractData.Required(LatestVersion);
    public override string ToString() => "XaiSkill { <metadata> }";
}

public sealed record XaiSkillDeletion(string Id, [property: JsonRequired] bool Deleted)
{
    [JsonRequired] public string Id { get; init => field = FileContractData.Identity(value); } = FileContractData.Identity(Id);
    public override string ToString() => "XaiSkillDeletion { <result> }";
}

public sealed record XaiSkillListRequest
{
    public int Limit { get; init => field = FileContractData.Limit(value); } = 100;
    public string? After { get; init; }
    public LlmFileOrder? Order { get; init; }
    public override string ToString() => "XaiSkillListRequest { <request> }";
}

public sealed record XaiSkillPage(IReadOnlyList<XaiSkill> Items, string? FirstId,
    string? LastId, [property: JsonRequired] bool HasMore)
{
    [JsonRequired] public IReadOnlyList<XaiSkill> Items { get; init => field = FileContractData.Copy(value); } = FileContractData.Copy(Items);
    public override string ToString() => "XaiSkillPage { <page> }";
}

public sealed record XaiSkillCapabilities
{
    public bool ResourceOperations { get; init; }
    public bool GenerationAttachment { get; init; }
}

/// <summary>Borrowed runtime upload input. It is not a portable data contract.</summary>
public abstract record XaiSkillUpload
{
    private protected XaiSkillUpload() { }
    public override string ToString() => "XaiSkillUpload { <borrowed> }";
}

public sealed record XaiSkillZipUpload(string FileName, Stream Content) : XaiSkillUpload
{
    public string FileName { get; init => field = FileContractData.Required(value); } = FileContractData.Required(FileName);
    public Stream Content { get; init => field = FileContractData.Required(value); } = FileContractData.Required(Content);
    public override string ToString() => "XaiSkillZipUpload { <borrowed> }";
}

public sealed record XaiSkillDirectoryFile(string RelativePath, Stream Content)
{
    public string RelativePath { get; init => field = FileContractData.Required(value); } = FileContractData.Required(RelativePath);
    public Stream Content { get; init => field = FileContractData.Required(value); } = FileContractData.Required(Content);
    public override string ToString() => "XaiSkillDirectoryFile { <borrowed> }";
}

public sealed record XaiSkillDirectoryUpload(IReadOnlyList<XaiSkillDirectoryFile> Files) : XaiSkillUpload
{
    public IReadOnlyList<XaiSkillDirectoryFile> Files { get; init => field = FileContractData.Copy(value); } = FileContractData.Copy(Files);
    public override string ToString() => "XaiSkillDirectoryUpload { <borrowed> }";
}

/// <summary>Explicit skill resources. Upload inputs and download destinations remain borrowed.</summary>
public interface IXaiSkillClient
{
    Task<XaiSkill> UploadAsync(string connectionId, XaiSkillUpload upload, CancellationToken cancellationToken = default);
    Task<XaiSkillPage> ListAsync(string connectionId, XaiSkillListRequest request, CancellationToken cancellationToken = default);
    Task<XaiSkill> GetAsync(string connectionId, string skillId, CancellationToken cancellationToken = default);
    Task<ResourceDownloadReceipt> DownloadAsync(string connectionId, string skillId, Stream destination, CancellationToken cancellationToken = default);
    Task<XaiSkillDeletion> DeleteAsync(string connectionId, string skillId, CancellationToken cancellationToken = default);
}
