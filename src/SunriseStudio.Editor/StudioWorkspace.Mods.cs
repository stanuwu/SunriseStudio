using SunriseStudio.Authoring;
using SunriseStudio.Live;
using SunriseStudio.Missions;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    public ModInstallResult? LastInstall { get; private set; }

    /** The package a new added asset defaults to, in the four hex digits the dialogs show. */
    public string DefaultPackageText => Project is { } project && Catalog is { } catalog
        ? project.DefaultAssetPackage(catalog).ToString("X4") : "0AA0";

    public void SetModMetadata(ModMetadata? metadata)
    {
        RequireEditable();
        Project!.SetMod(metadata);
        ProjectRevision++;
        if (Project.ConfigBindings.Count > 0 && Document is { } document)
        {
            if (document.AddedAssetId is { } id) SelectAdded(id, document.OpenContext);
            else Select(document.Entry.TagHash, true, document.OpenContext);
        }
        Status = "Mod details saved";
        Changed?.Invoke();
    }

    /**
     * Rebuilds saved tables that still carry old copies of grown arrays, so a build ships each once.
     * @return The tables it rewrote.
     */
    public IReadOnlyCollection<uint> CompactSavedTables()
    {
        RequireEditable();
        var edits = SavedTableCompaction.Plan(Project!, Catalog!);
        if (edits.Count == 0) return [];
        MutateProject(() => Project!.ApplyEdits(edits, Catalog!), edits.Keys);
        ProjectRevision++;
        return edits.Keys.ToArray();
    }

    /** Saved mission scripts join the install, so the caller settles an unsaved mission draft first. */
    public void BuildAndInstall()
    {
        RequireEditable();
        CompactSavedTables();
        var project = Project!;
        var catalog = Catalog!;
        BeginWork("Installing mod", () =>
        {
            var result = ModInstaller.BuildAndInstall(project, catalog, MissionProject.ModScripts(project), ReportProgress, TagSchema);
            return () =>
            {
                LastInstall = result;
                if (result.Build is not null) LastBuild = result.Build;
                Status = "Mod installed";
                Notice = new("Mod installed. Restart the game to load it.", false, result.ModDirectory,
                    string.Join("\n", (result.Build?.Notes ?? []).Prepend(
                        $"{result.PatchFiles:N0} patches, {result.PackageFiles:N0} packages, {result.ScriptFiles:N0} scripts, "
                        + $"{result.SdkFiles:N0} SDK modules")));
                Changed?.Invoke();
            };
        }, blocking: false);
    }

    /** The last live update and what the game did with each tag. */
    public LiveUpdateReport? LastLiveUpdate { get; private set; }
    /** The project revision the last live update started from; -1 before the first. */
    public int LivePushedRevision { get; private set; } = -1;

    /**
     * Writes changed packages as their next generations and changed scripts into the installed mod,
     * then asks the running game to load them.
     * @param automatic Set by the Live toggle: an update with nothing changed then sends nothing.
     */
    public void UpdateRunningGame(bool automatic = false)
    {
        RequireEditable();
        CompactSavedTables();
        var project = Project!;
        var catalog = Catalog!;
        int revision = LivePushedRevision = ProjectRevision;
        long started = Environment.TickCount64;
        BeginWork("Updating running game", () =>
        {
            var games = LiveConnection.FindProcesses(catalog.GameRoot);
            if (games.Count == 0) throw new InvalidOperationException("The game is not running. Use Build and install.");
            if (games.Count > 1) throw new InvalidOperationException("More than one game is running from this installation.");
            var result = ModInstaller.BuildAndPushLive(project, catalog, MissionProject.ModScripts(project), TagSchema, ReportProgress);
            var tags = new List<LiveTagStatus>();
            var lines = new List<string>();
            bool failed = false;
            using (var link = new LiveCommandConnection(games[0].Id))
            {
                if (result.Tags.Count > 0)
                {
                    ReportProgress("Waiting for the game to load the new packages");
                    var entries = result.Tags.Select(Entry).ToArray();
                    // patchTags also does everything reloadTags does, so one request carries both.
                    var reply = (result.Tags.Any(tag => tag.Ranges.Count > 0)
                        ? link.PatchTagsAsync(result.ModId, result.Revision, entries, OperationToken)
                        : link.ReloadTagsAsync(result.ModId, result.Revision, entries, OperationToken)).GetAwaiter().GetResult();
                    for (int index = 0; index < result.Tags.Count; index++)
                        tags.Add(new(result.Tags[index], index < reply.Results.Count ? reply.Results[index] : NoResult));
                    failed |= reply.Reply.Status != LiveCommandStatus.Ok;
                    lines.Add(DescribeTags(reply.Reply, tags));
                }
                if (result.ScriptWrites + result.ScriptDeletes > 0 || result.Tags.Count == 0 && !automatic)
                {
                    ReportProgress("Waiting for the game to reload the mod");
                    var reply = link.ReloadModAsync(result.ModId, OperationToken).GetAwaiter().GetResult();
                    failed |= reply.Status != LiveCommandStatus.Ok;
                    lines.Add(LiveCommandProtocol.Describe(reply));
                }
            }
            if (lines.Count == 0) lines.Add("Nothing changed since the last update.");
            string text = string.Join(" ", lines);
            var report = new LiveUpdateReport(revision, result.Revision, tags, text, failed,
                TimeSpan.FromMilliseconds(Environment.TickCount64 - started));
            return () =>
            {
                if (result.Build is not null) LastBuild = result.Build;
                LastLiveUpdate = report;
                Status = text;
                if (failed) Error = text;
                Notice = new(text, failed, result.ModDirectory,
                    result.Build?.Notes.Count > 0 ? string.Join("\n", result.Build.Notes) : null);
                Changed?.Invoke();
            };
        }, blocking: false);
    }

    /** Asks the running game to land in the player's current bubble on its next launch. */
    public void ReloadBubble()
    {
        RequireEditable();
        string modId = Project!.Mod?.Id ?? throw new InvalidOperationException("Set the mod details first.");
        string root = Catalog!.GameRoot;
        BeginWork("Reloading bubble", () =>
        {
            var games = LiveConnection.FindProcesses(root);
            if (games.Count != 1) throw new InvalidOperationException(games.Count == 0
                ? "The game is not running." : "More than one game is running from this installation.");
            LiveCommandReply reply;
            using (var link = new LiveCommandConnection(games[0].Id))
                reply = link.ReloadBubbleAsync(modId, null, OperationToken).GetAwaiter().GetResult();
            // The game's message says where the next launch lands.
            string text = reply.Message.Length != 0 ? reply.Message : LiveCommandProtocol.Describe(reply);
            bool failed = reply.Status != LiveCommandStatus.Ok;
            return () =>
            {
                Status = text;
                if (failed) Error = text;
                Notice = new(text, failed);
                Changed?.Invoke();
            };
        }, blocking: false);
    }

    private static readonly LiveReloadResult NoResult = new(LiveReloadOutcome.None, 0, 0);

    /**
     * A tag with ranges is a patch: the game writes it into the tag when it next loads it, and into
     * the copy it holds now when the ranges are poke-safe. The rest replace the tag.
     */
    private static LiveReloadEntry Entry(LiveTag tag)
    {
        bool patch = tag.Ranges.Count > 0;
        bool pokeSafe = patch && tag.Tier is 2 or 3;
        byte flags = (byte)((tag.LayoutPreserving ? LiveReloadEntry.LayoutPreserving : 0) | (pokeSafe ? LiveReloadEntry.PokeSafe : 0)
            | (pokeSafe && tag.SpawnTime ? LiveReloadEntry.SpawnTime : 0));
        var kind = tag.Removed ? LiveEditKind.Removed : patch ? LiveEditKind.Patch : LiveEditKind.Replace;
        return new(tag.Tag, tag.ClassId, kind, flags, tag.NewSize, tag.PackageId, (uint)tag.EntryIndex,
            tag.Ranges.Select(range => new LivePatchRange((uint)range.Offset, range.Bytes, Crc32.Compute(range.Old))).ToArray());
    }

    /** One line: how many tags each outcome took, or the game's message when it refused the request. */
    private static string DescribeTags(LiveCommandReply reply, IReadOnlyList<LiveTagStatus> tags)
    {
        if (reply.Status != LiveCommandStatus.Ok)
            return reply.Message.Length != 0 ? reply.Message : "The game did not load the new packages.";
        var counts = tags.GroupBy(tag => tag.Result.Outcome).OrderBy(group => group.Key)
            .Select(group => $"{group.Count()} {LiveUpdateReport.Describe(group.Key)}");
        return "Tags: " + string.Join(", ", counts) + ".";
    }
}

/** One changed tag and what the game did with it. */
public sealed record LiveTagStatus(LiveTag Tag, LiveReloadResult Result);

/** One live update: the project revision it built, the mod's live revision, and the game's answers. */
public sealed record LiveUpdateReport(int ProjectRevision, uint Revision, IReadOnlyList<LiveTagStatus> Tags, string Message, bool Failed,
    TimeSpan Elapsed)
{
    public static string Describe(LiveReloadOutcome outcome) => outcome switch
    {
        LiveReloadOutcome.Applied => "applied",
        LiveReloadOutcome.NextSpawn => "next spawn",
        LiveReloadOutcome.NeedsBubbleReload => "need Reload bubble",
        LiveReloadOutcome.NeedsRestart => "need a restart",
        LiveReloadOutcome.Resident => "loaded",
        LiveReloadOutcome.Refused => "refused",
        LiveReloadOutcome.GuardMismatch => "changed in the game",
        LiveReloadOutcome.NotResident => "not loaded",
        _ => "no answer"
    };
}
