using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// The ability tools a Codex or Claude conversation adds after the Host's tools: <c>update_plan</c>, and
/// <c>load_skill</c> when a skill folder holds a SKILL.md. Inference answers both; every other call goes to the Host's
/// handler. The Host sees each call as an ordinary tool event.
/// </summary>
internal static class AgentAbilityTools
{
    internal const string PlanTool = "update_plan";
    internal const string SkillTool = "load_skill";

    private const string PlanDescription = "Updates the task plan. Send the whole plan each time, with at most one step in_progress.";
    private const string PlanResult = "Plan updated.";
    private const string SkillDescription = "Loads the instructions of one skill listed in your instructions.";
    private const string SkillFile = "SKILL.md";

    // The Codex CLI's plan tool shape, which Codex models are trained on.
    private const string PlanSchema = """
        {
          "type": "object",
          "properties": {
            "explanation": { "type": "string" },
            "plan": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "step": { "type": "string" },
                  "status": { "type": "string", "enum": ["pending", "in_progress", "completed"] }
                },
                "required": ["step", "status"]
              }
            }
          },
          "required": ["plan"]
        }
        """;

    /// <summary>Adds update_plan, and load_skill when skills exist, and wraps the handler.</summary>
    internal static LlmAgentSessionOptions Apply(LlmAgentSessionOptions options)
    {
        // Returned calls are never run, so MCP sampling gets no ability tools.
        if (options.ToolHandling == LlmAgentToolHandling.ReturnRequests) return options;
        HashSet<string> hostNames = [.. ClientTools.Functions(options.Tools).Select(tool => tool.Name)];
        // The Host's tool of either name comes first, so the ability tool of that name is not added.
        bool plan = !hostNames.Contains(PlanTool);
        IReadOnlyList<Skill> skills = hostNames.Contains(SkillTool) ? [] : ReadSkills(options.SkillFolders);
        List<LlmAgentClientToolDefinition> tools = [.. options.Tools];
        if (plan)
            tools.Add(new LlmAgentToolDefinition { Name = PlanTool, Description = PlanDescription, InputSchema = JsonElement.Parse(PlanSchema) });
        if (skills.Count > 0)
            tools.Add(new LlmAgentToolDefinition { Name = SkillTool, Description = SkillDescription, InputSchema = SkillSchema(skills) });
        LlmAgentToolHandler? host = options.ToolHandler;
        return options with
        {
            Tools = tools.AsReadOnly(),
            Instructions = skills.Count > 0 ? WithSkills(options.Instructions, skills) : options.Instructions,
            ToolHandler = (call, cancellationToken) => call.Name switch
            {
                PlanTool when plan => Task.FromResult(LlmToolOutput.FromText(PlanResult)),
                SkillTool when skills.Count > 0 => LoadAsync(skills, call.Arguments, cancellationToken),
                _ => host?.Invoke(call, cancellationToken) ?? Task.FromResult(LlmToolOutput.FromText(ClientTools.NoHandler(call.Name), isError: true))
            }
        };
    }

    /// <summary>The skills of the folders that hold a readable SKILL.md, in folder order; the first of a name is used.</summary>
    private static List<Skill> ReadSkills(IReadOnlyList<string> folders)
    {
        var skills = new List<Skill>();
        foreach (string folder in folders)
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(folder, SkillFile)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            (string name, string description) = Describe(text, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
            if (skills.Any(skill => skill.Name == name)) continue;
            skills.Add(new Skill(name, description, Path.GetFullPath(folder)));
        }
        return skills;
    }

    /// <summary>
    /// The frontmatter name, else the folder name; the frontmatter description on one line, else the first non-empty
    /// body line.
    /// </summary>
    private static (string Name, string Description) Describe(string text, string folderName)
    {
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        string? name = null;
        string? description = null;
        int body = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            int end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
            if (end > 0)
            {
                foreach (string line in lines[1..end])
                {
                    int colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon < 0) continue;
                    string key = line[..colon].Trim();
                    string value = line[(colon + 1)..].Trim().Trim('"', '\'');
                    if (key == "name" && value.Length > 0) name ??= value;
                    else if (key == "description" && value.Length > 0) description ??= value;
                }
                body = end + 1;
            }
        }
        description ??= lines.Skip(body).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        return (name ?? folderName, description);
    }

    private static JsonElement SkillSchema(IReadOnlyList<Skill> skills) => JsonElement.Parse(new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray([.. skills.Select(skill => JsonValue.Create(skill.Name))])
            }
        },
        ["required"] = new JsonArray("name")
    }.ToJsonString());

    private static string WithSkills(string? instructions, IReadOnlyList<Skill> skills)
    {
        var section = new StringBuilder("Skills are available. Before a task one of them covers, call load_skill with its name.");
        foreach (Skill skill in skills) section.Append('\n').Append("- ").Append(skill.Name).Append(": ").Append(skill.Description);
        return string.IsNullOrEmpty(instructions) ? section.ToString() : $"{instructions}\n\n{section}";
    }

    /// <summary>The SKILL.md text, read at call time, then the skill's folder.</summary>
    private static async Task<LlmToolOutput> LoadAsync(IReadOnlyList<Skill> skills, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        // The argument check ran first, so the name is one of the listed skills.
        string name = arguments.GetProperty("name").GetString()!;
        Skill skill = skills.First(item => item.Name == name);
        try
        {
            string text = await File.ReadAllTextAsync(Path.Combine(skill.Folder, SkillFile), cancellationToken);
            return LlmToolOutput.FromText($"{text.TrimEnd('\n')}\nSkill folder: {skill.Folder}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return LlmToolOutput.FromText($"Skill '{name}' could not be read: {error.Message}", isError: true);
        }
    }

    private sealed record Skill(string Name, string Description, string Folder);
}
