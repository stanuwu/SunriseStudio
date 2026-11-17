using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

internal sealed partial class LiveInvestmentSection
{
    // Text lines the chosen action's details take under the list: operation id, summary, target and the receipt's note.
    private const int DetailLines = 4;
    private ulong? selectedOperation;

    private string? ActivityRow => selectedOperation is { } id ? $"op:{id:X16}" : null;

    private void ShowActivityRow(string row)
    {
        if (row.StartsWith("op:", StringComparison.Ordinal)
            && ulong.TryParse(row.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id))
            selectedOperation = id;
    }

    /** Lists every action sent in this Studio run, newest first, with its result. The chosen action's details follow the list. */
    private void DrawActivity(LiveSectionContext context, InvestmentCatalog? catalog)
    {
        var live = workspace.LiveInvestment;
        var operations = live.Operations;
        ImGui.TextDisabled(operations.Count == 0 ? "Nothing sent to the game in this Studio run."
            : operations.Count == 1 ? "1 action this session" : $"{operations.Count:N0} actions this session");
        float line = ImGui.GetTextLineHeightWithSpacing();
        // The details keep their room whether or not an action is chosen, so the list never moves on a click.
        float details = DetailLines * line + ImGui.GetFrameHeightWithSpacing();
        var size = ImGui.GetContentRegionAvail();
        if (ImGui.BeginTable("live-activity", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new(size.X, Math.Max(3 * line, size.Y - details))))
        {
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("00:00:00").X);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("To", ImGuiTableColumnFlags.WidthStretch, 2);
            ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            DrawActivityRows(live, operations);
            ImGui.EndTable();
        }
        DrawOperation(context, catalog, operations.FirstOrDefault(row => row.Identity.Id == selectedOperation));
    }

    private unsafe void DrawActivityRows(LiveInvestmentSession live, IReadOnlyList<LiveOperation> operations)
    {
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(operations.Count);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var operation = operations[operations.Count - 1 - i];
                ImGui.PushID(i);
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                string time = operation.SubmittedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                // Check result sits in the Result cell, so the row's selectable lets it take the click.
                if (ImGui.Selectable(time, operation.Identity.Id == selectedOperation,
                    ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
                    selectedOperation = operation.Identity.Id;
                ImGui.TableNextColumn(); ImGui.TextUnformatted(operation.Action.Summary);
                ImGui.TableNextColumn(); ImGui.TextDisabled(operation.Target);
                ImGui.TableNextColumn(); LiveOperationLine.Draw(live, operation);
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
    }

    /** Draws the chosen action's details and an Open item button for the item it names. */
    private static void DrawOperation(LiveSectionContext context, InvestmentCatalog? catalog, LiveOperation? operation)
    {
        if (operation is null) { ImGui.TextDisabled("Choose an action to see its details."); return; }
        ImGui.TextUnformatted(LiveOperationLine.Tooltip(operation));
        uint? hash = ItemOf(operation.Action);
        var item = hash is { } named ? catalog?.FindByHash(named) : null;
        ImGui.BeginDisabled(item is null);
        if (ImGui.Button("Open item") && item is not null) context.OpenAsset(item.Tag, ItemPage, null);
        ImGui.EndDisabled();
        StudioUi.Tip(item is not null ? "Open in Assets" : hash is null ? "This action names no item."
            : catalog is null ? "Reading items." : LiveItemActions.NotInCatalog);
    }

    /** The item an action names, if any. */
    private static uint? ItemOf(LiveAction action) => action switch
    {
        LiveGrantItem grant => grant.ItemHash,
        LiveSetQuantity quantity => quantity.Row.DefinitionHash,
        LiveClaimPassRow claim => claim.ItemHash,
        LiveResetPassClaim reset => reset.ItemHash,
        LiveSetObjective objective => objective.Held.ItemHash,
        LiveCompleteObjectives complete => complete.Held.ItemHash,
        _ => null
    };
}
