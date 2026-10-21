using System.Numerics;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** The Connect bubbles dialog: two bubbles of the open map, two exit boxes, a profile and a policy for existing zones. */
public sealed partial class StudioForm
{
    private const string CrossingLimits = StudioUi.MayNotWork + "\nWalk-across only: no doors, teleports, scripted travel or entry points."
        + "\nOne zone per bubble pair. Exits stay axis-aligned."
        + "\nRetail rebuilds both bubbles' cells as one zone and drops occlusion baked into them."
        + "\nContainer masks are not updated.";
    private bool openCrossings;
    private IReadOnlyList<CrossingBubble> crossingBubbles = [];
    private uint crossingMap;
    private int crossingSource = -1, crossingDestination = -1, crossingProfile, crossingPolicy;
    private Vector3 crossingSourceCenter, crossingDestinationCenter, crossingSourceHalf = new(2), crossingDestinationHalf = new(2);
    private string crossingId = "", crossingError = "";

    private void BeginCrossings()
    {
        if (workspace.World is not { } world) return;
        crossingError = "";
        try { crossingBubbles = workspace.CrossingBubbles(world.Map.Tag); }
        catch (InvalidDataException error) { crossingBubbles = []; crossingError = error.Message; }
        crossingMap = world.Map.Tag;
        crossingSource = crossingDestination = -1;
        crossingId = $"connection-{workspace.BubbleConnections.Count + 1}";
        openCrossings = true;
    }

    private string CrossingBubbleName(CrossingBubble bubble) =>
        $"{workspace.ResolveHash(bubble.NameHash) ?? $"{bubble.NameHash:X8}"}##{bubble.NameHash:X8}";

    private void CrossingBubbleChoice(string label, ref int chosen, ref Vector3 center)
    {
        ImGui.SetNextItemWidth(260 * scale);
        if (!ImGui.BeginCombo(label, chosen >= 0 ? CrossingBubbleName(crossingBubbles[chosen]) : "Choose a bubble")) return;
        for (int index = 0; index < crossingBubbles.Count; index++)
        {
            var bubble = crossingBubbles[index];
            ImGui.BeginDisabled(bubble.Refusal.Length != 0);
            if (ImGui.Selectable(CrossingBubbleName(bubble), index == chosen))
            {
                chosen = index;
                center = (bubble.Minimum + bubble.Maximum) / 2;
            }
            ImGui.EndDisabled();
            StudioUi.Tip(bubble.Refusal.Length != 0 ? bubble.Refusal : $"{bubble.Layers} layers, {bubble.Zones} existing crossing zones");
        }
        ImGui.EndCombo();
    }

    private void DrawCrossings()
    {
        if (openCrossings) { ImGui.OpenPopup("Connect bubbles"); openCrossings = false; }
        if (!ImGui.BeginPopupModal("Connect bubbles", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted("Walk-across crossing between two bubbles");
        StudioUi.Wip(CrossingLimits);
        ImGui.SetNextItemWidth(260 * scale);
        ImGui.InputText("Name", ref crossingId, ProjectBubbleConnection.MaximumIdLength);
        CrossingBubbleChoice("From", ref crossingSource, ref crossingSourceCenter);
        ImGui.InputFloat3("From exit centre", ref crossingSourceCenter);
        ImGui.InputFloat3("From exit half size", ref crossingSourceHalf);
        CrossingBubbleChoice("To", ref crossingDestination, ref crossingDestinationCenter);
        ImGui.InputFloat3("To exit centre", ref crossingDestinationCenter);
        ImGui.InputFloat3("To exit half size", ref crossingDestinationHalf);
        ImGui.SetNextItemWidth(260 * scale);
        ImGui.Combo("Profile", ref crossingProfile, ["Retail", "Simple"], 2);
        StudioUi.Help("Retail rebuilds both bubbles' cells: own exit 2, middle 3, other exit 4, rest 5.\n"
            + "Simple keeps the installed cells and edits only the cells inside the two exits.");
        ImGui.SetNextItemWidth(260 * scale);
        ImGui.Combo("Existing crossings", ref crossingPolicy, ["Keep", "Deactivate"], 2);
        StudioUi.Help("Keep leaves the bubbles' installed zones. Deactivate removes them; their cells become inert.\n"
            + "Retarget is not supported.");
        if (crossingError.Length > 0) ImGui.TextWrapped(crossingError);
        var saved = workspace.BubbleConnections.Where(c => c.MapTag == crossingMap).ToArray();
        if (saved.Length > 0)
        {
            StudioUi.Heading("Saved connections");
            foreach (var connection in saved)
            {
                ImGui.PushID(connection.Id);
                ImGui.TextUnformatted($"{connection.Id}: {BubbleLabel(connection.SourceBubble)} to {BubbleLabel(connection.DestinationBubble)}, "
                    + $"{connection.Profile}, {connection.Policy}");
                ImGui.SameLine();
                ImGui.BeginDisabled(!workspace.CanEdit);
                if (ImGui.SmallButton("Remove"))
                    TryCrossings(() => workspace.ApplyBubbleConnections([.. workspace.BubbleConnections.Where(c => c.Id != connection.Id)],
                        $"Removed {connection.Id}"));
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }
        ImGui.Separator();
        bool ready = workspace.CanEdit && crossingSource >= 0 && crossingDestination >= 0 && crossingSource != crossingDestination;
        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("Connect")) TryCrossings(Connect);
        ImGui.EndDisabled();
        StudioUi.Tip(!workspace.CanEdit ? "Create a project to edit." : ready ? StudioUi.MayNotWork : "Choose two different bubbles.");
        ImGui.SameLine();
        if (ImGui.Button("Close")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private string BubbleLabel(uint name) => workspace.ResolveHash(name) ?? $"{name:X8}";

    private void Connect()
    {
        static ProjectCrossingBox Box(Vector3 center, Vector3 half) => new([center.X, center.Y, center.Z], [half.X, half.Y, half.Z]);
        var connection = new ProjectBubbleConnection(crossingId.Trim(), crossingMap, crossingBubbles[crossingSource].NameHash,
            crossingBubbles[crossingDestination].NameHash, Box(crossingSourceCenter, crossingSourceHalf),
            Box(crossingDestinationCenter, crossingDestinationHalf), (CrossingProfile)crossingProfile, (InheritedZonePolicy)crossingPolicy);
        connection.Validate();
        workspace.ApplyBubbleConnections([.. workspace.BubbleConnections.Where(c => c.Id != connection.Id), connection], $"Connected {connection.Id}");
        ImGui.CloseCurrentPopup();
    }

    private void TryCrossings(Action action)
    {
        try { crossingError = ""; action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException)
        { crossingError = error.Message; }
    }
}
