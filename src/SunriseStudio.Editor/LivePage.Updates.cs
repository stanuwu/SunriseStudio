using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Live;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

public sealed partial class LivePage
{
    // Edits arrive in bursts while a value is dragged; one update per settled revision.
    private const long LiveSettleMilliseconds = 750;
    private bool liveUpdates;
    private StudioProject? liveProject;
    /** The project revision waiting to be pushed and when it last changed. */
    private (int Revision, long Since)? pendingRevision;

    /** With Live on, pushes each settled project change to the running game. A new project turns Live off. */
    private void PollUpdates(StudioWorkspace workspace)
    {
        if (!ReferenceEquals(liveProject, workspace.Project)) { liveProject = workspace.Project; liveUpdates = false; }
        if (!liveUpdates || workspace.ProjectRevision == workspace.LivePushedRevision) { pendingRevision = null; return; }
        long now = Environment.TickCount64;
        if (pendingRevision?.Revision != workspace.ProjectRevision) { pendingRevision = (workspace.ProjectRevision, now); return; }
        if (now - pendingRevision.Value.Since < LiveSettleMilliseconds || !workspace.CanEdit || workspace.Project?.Mod is null) return;
        pendingRevision = null;
        workspace.Run(() => workspace.UpdateRunningGame(automatic: true));
    }

    /** The Live toggle, Reload bubble, and the last update's answer for each changed tag. */
    private void DrawUpdates(StudioWorkspace workspace)
    {
        bool mod = workspace.Project?.Mod is not null;
        ImGui.BeginDisabled(!mod);
        ImGui.Checkbox("Live", ref liveUpdates);
        ImGui.EndDisabled();
        StudioUi.Tip(mod ? "Push each saved change to the running game" : "Set the mod details first");
        ImGui.SameLine();
        ImGui.BeginDisabled(!mod || !workspace.CanEdit);
        if (ImGui.Button("Reload bubble")) workspace.Run(workspace.ReloadBubble);
        ImGui.EndDisabled();
        StudioUi.Tip("Land in the current bubble on the next launch from orbit");
        if (workspace.LastLiveUpdate is not { } report) return;
        ImGui.SameLine();
        ImGui.TextColored(report.Failed ? new Vector4(1, .6f, .35f, 1) : new Vector4(.45f, .85f, .5f, 1),
            $"{report.Message} ({report.Elapsed.TotalSeconds:F1} s)");
        if (report.Tags.Count == 0 || !ImGui.TreeNode($"Changed tags ({report.Tags.Count})###liveTags")) return;
        if (ImGui.BeginTable("##liveTags", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Tag", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Tier");
            ImGui.TableSetupColumn("Result");
            ImGui.TableHeadersRow();
            foreach (var status in report.Tags)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(workspace.DisplayName(status.Tag.Tag));
                StudioUi.Tip($"{status.Tag.Tag:X8}, class {status.Tag.ClassId:X8}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(status.Tag.Tier.ToString(CultureInfo.InvariantCulture));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Result(status.Result));
            }
            ImGui.EndTable();
        }
        ImGui.TreePop();
    }

    private static string Result(LiveReloadResult result) => result.Reason switch
    {
        LiveReloadDetail.StageFailed => "refused: the game could not stage the packages",
        LiveReloadDetail.TagMissing => "refused: the game does not serve this tag",
        LiveReloadDetail.PatchUnsupported => "refused: the patch does not fit the tag",
        LiveReloadDetail.ClassMismatch => "refused: the game holds another class there",
        LiveReloadDetail.WriteFailed => "refused: the write did not hold",
        _ => LiveUpdateReport.Describe(result.Outcome)
    };
}
