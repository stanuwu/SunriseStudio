/**
 * File: The model team configuration (Settings > Models & workflows): models, ordered role ladders and subagent limits.
 * Ported from solforge-next (Solforge.Next.Harness.HarnessSettings). Credentials are referenced by environment variable
 * NAME only; this schema has no API key, token, password or subscription credential fields.
 */
namespace Solforge.Harness;

/** How a model is reached. PiRpc is kept for settings compatibility; no adapter is registered for it. */
public enum HarnessTransport
{
    CodexSubscription,
    ClaudeSubscription,
    GoogleSubscription,
    OpenAiCompatible,
    PiRpc
}

/** Descriptive only: it never adds models or reorders a ladder. */
public enum HarnessModelTier { Frontier, Executor, Small }

/** The roles a workflow routes; the three Subagent roles have their own ladders. */
public enum HarnessRole
{
    Planner, Executor, SmallFix, Reviewer,
    SubagentPlanner, SubagentExecutor, SubagentReviewer
}

/**
 * UI-editable configuration. In the Solforge editor the credential file, the shared resources collection and the
 * Workbench root come from StudioSettings (CredentialFile, SharedResourcesRoot, WorkbenchRoot) and are copied in before
 * use; the fields stay here so a settings file written by solforge-next has the same shape.
 */
public sealed class HarnessSettings
{
    public string? CredentialFilePath { get; set; }
    public string? AiRuntimePath { get; set; }
    /** User-selected collection root; blank disables the shared collection. */
    public string? SharedResourcesPath { get; set; } = Shared.SharedHarnessResources.DefaultSharedResourcesRoot;
    /** Existing Workbench root containing config, memory and artifacts; never a drive root. */
    public string? WorkbenchRoot { get; set; }
    public List<HarnessModel> Models { get; set; } = [];
    public List<HarnessRoleRoute> Routes { get; set; } =
        Enum.GetValues<HarnessRole>().Select(role => new HarnessRoleRoute { Role = role }).ToList();
    public int MaxParallelSubagents { get; set; } = 4;
    public int MaxSubagentDepth { get; set; } = 3;

    /** The ladder of one role (created empty when the file had none). */
    public HarnessRoleRoute Route(HarnessRole role)
    {
        if (Routes.FirstOrDefault(route => route?.Role == role) is { } found) return found;
        var created = new HarnessRoleRoute { Role = role };
        Routes.Add(created);
        return created;
    }

    /** A deep copy, so the UI can edit without touching a running workflow's snapshot. */
    public HarnessSettings Clone() => new()
    {
        CredentialFilePath = CredentialFilePath, AiRuntimePath = AiRuntimePath, SharedResourcesPath = SharedResourcesPath,
        WorkbenchRoot = WorkbenchRoot, MaxParallelSubagents = MaxParallelSubagents, MaxSubagentDepth = MaxSubagentDepth,
        Models = Models.Where(model => model is not null).Select(model => model.Copy()).ToList(),
        Routes = Routes.Where(route => route is not null).Select(route => new HarnessRoleRoute { Role = route.Role, ModelIds = [.. route.ModelIds ?? []] }).ToList()
    };
}

/** One configured model. ApiKeyVariable is a variable NAME looked up in the credential file at call time. */
public sealed class HarnessModel
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public HarnessTransport Transport { get; set; }
    public string Provider { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string? BaseUrl { get; set; }
    public string? ApiKeyVariable { get; set; }
    public bool Enabled { get; set; } = true;
    public HarnessModelTier Tier { get; set; } = HarnessModelTier.Executor;

    public HarnessModel Copy() => (HarnessModel)MemberwiseClone();
}

/**
 * ModelIds is the exact ordered ladder (the settings page reorders it by drag and drop).
 * Tier is descriptive; it never adds models or changes this order.
 */
public sealed class HarnessRoleRoute
{
    public HarnessRole Role { get; set; }
    public List<string> ModelIds { get; set; } = [];
}
