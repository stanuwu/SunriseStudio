using ImGuiNET;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

public sealed class RelatedAssetsPage : IStudioPage
{
    private string search = "", appliedSearch = "";
    private AssetConnections? previous;
    private RelatedAsset[] rows = [];
    public string Id => "sunrise.related";
    public string Title => "Related";
    public bool CanOpen(StudioDocument document) => document.Connections is not null;

    /** Typed links retain their owner context when opening models or animation clips. */
    public unsafe void Draw(StudioPageContext context)
    {
        if (context.Workspace.Document?.Connections is not { } connections) return;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##relatedSearch", "Find connected asset", ref search, 256);
        if (!ReferenceEquals(previous, connections) || search != appliedSearch)
        {
            rows = connections.Related.Where(link => search.Length == 0
                || link.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || link.Role.Contains(search, StringComparison.OrdinalIgnoreCase)
                || link.Kind.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            previous = connections; appliedSearch = search;
        }
        ImGui.TextDisabled($"{rows.Length:N0} connected assets");
        if (connections.Diagnostics.Count > 0 && ImGui.CollapsingHeader($"Notes ({connections.Diagnostics.Count})###notes"))
            StudioUi.Facts("connection-notes", connections.Diagnostics);
        if (!ImGui.BeginTable("connections", 2, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable,
            ImGui.GetContentRegionAvail())) return;
        ImGui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Connection", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rows.Length);
        while (clipper.Step())
            for (int index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var row = rows[index];
                string kind = KindLabel(row.Kind), name = ShortName(row.Name);
                ImGui.PushID(index); ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.Selectable(name, false, ImGuiSelectableFlags.SpanAllColumns))
                    context.Workspace.SelectRelated(row);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{row.Name}\n{row.TagHash:X8}\n{kind}");
                ImGui.TableNextColumn();
                // A role that only repeats the name says nothing; the kind does.
                ImGui.TextUnformatted(row.IsCandidate ? "Possible reference"
                    : row.Role.Length == 0 || name.Equals(row.Role, StringComparison.OrdinalIgnoreCase) ? kind : row.Role);
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy(); ImGui.EndTable();
    }

    private static string KindLabel(AssetRelationshipKind kind) => kind switch
    {
        AssetRelationshipKind.AnimationDefinition => "Animation definition",
        AssetRelationshipKind.AnimationClip => "Animation clip",
        _ => kind.ToString()
    };

    /** Drops a trailing " [XXXXXXXX]" tag; the tooltip still shows it. */
    private static string ShortName(string name)
    {
        int open = name.LastIndexOf(" [", StringComparison.Ordinal);
        return open > 0 && name.EndsWith(']') && name.Length - open == 11
            && name.AsSpan(open + 2, 8).ToString().All(Uri.IsHexDigit) ? name[..open] : name;
    }
}
