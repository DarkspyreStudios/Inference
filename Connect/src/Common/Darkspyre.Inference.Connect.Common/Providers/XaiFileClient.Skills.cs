using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed partial class XaiFileClient
{
    internal Task<XaiSkillPage> ListSkillsAsync(LlmConnection connection, XaiSkillListRequest request,
        Action fence, CancellationToken token)
    {
        string path = SkillListPath(request);
        int limit = request.Limit;
        return JsonAsync(connection, HttpMethod.Get, path, null, root =>
        {
            if (Text(root, "object") != "list") throw Invalid();
            JsonElement data = Required(root, "data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > limit) throw Invalid();
            XaiSkill[] items = data.EnumerateArray().Select(ReadSkill).ToArray();
            string? first = OptionalString(root, "first_id");
            string? last = OptionalString(root, "last_id");
            bool more = Boolean(root, "has_more");
            if (items.Length > 0)
            {
                _ = XaiFileRequestData.Identity(first ?? throw Invalid());
                _ = XaiFileRequestData.Identity(last ?? throw Invalid());
            }
            if (more) _ = XaiFileRequestData.Identity(last ?? throw Invalid());
            return new XaiSkillPage(items, first, last, more);
        }, fence, token);
    }

    internal Task<XaiSkill> GetSkillAsync(LlmConnection connection, string skillId, Action fence, CancellationToken token) =>
        JsonAsync(connection, HttpMethod.Get, SkillPath(skillId), null, ReadSkill, fence, token);

    internal Task<XaiSkillDeletion> DeleteSkillAsync(LlmConnection connection, string skillId, Action fence, CancellationToken token) =>
        JsonAsync(connection, HttpMethod.Delete, SkillPath(skillId), null, root =>
        {
            if (Text(root, "object") != "skill.deleted") throw Invalid();
            return new XaiSkillDeletion(XaiFileRequestData.Identity(Text(root, "id")), Boolean(root, "deleted"));
        }, fence, token);

    internal Task<ResourceDownloadReceipt> DownloadSkillAsync(LlmConnection connection, string skillId, Stream destination,
        Action fence, CancellationToken token) =>
        DownloadCoreAsync(connection, () => SkillDownloadPath(skillId, destination),
            (_, _, _) => Task.FromResult(destination), fence, token, accept: "application/zip");

    internal static string SkillPath(string skillId) => "skills/" + Uri.EscapeDataString(XaiFileRequestData.Identity(skillId));

    internal static string SkillDownloadPath(string skillId, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Download destination must be writable.", nameof(destination));
        return SkillPath(skillId) + "/content";
    }

    internal static string SkillListPath(XaiSkillListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int limit = FileContractData.Limit(request.Limit);
        string path = "skills?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (request.After is { } after) path += "&after=" + Uri.EscapeDataString(after);
        if (request.Order is { } order) path += "&order=" + (order switch
        {
            LlmFileOrder.Ascending => "asc",
            LlmFileOrder.Descending => "desc",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        });
        return path;
    }

    private static XaiSkill ReadSkill(JsonElement root)
    {
        if (Text(root, "object") != "skill") throw Invalid();
        return new XaiSkill(XaiFileRequestData.Identity(Text(root, "id")),
            DateTimeOffset.FromUnixTimeSeconds(Integer(Required(root, "created_at"))),
            Text(root, "name"), Text(root, "description"), Text(root, "default_version"), Text(root, "latest_version"));
    }
}
