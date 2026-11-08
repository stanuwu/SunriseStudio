using System.Globalization;
using ImGuiNET;
using SunriseStudio.Interchange;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private bool importBundleDialog;
    private string bundlePath = "", bundleName = "", bundlePackage = "0C00";
    private int bundleMode, observedTransferRevision;
    private WorldSceneExportResult? observedWorldExport;

    // A map root is too large for one glTF scene; maps export through the world export.
    private bool CanExportBundle => !workspace.IsBusy && workspace.Document?.Entry is { TagHash: > 0, Reference: not SMapRoot.Class };
    private const string MapBundleTip = "A map is too big for one bundle. Use World to Blender.";

    /** Explains a disabled full-asset export when the open document is a map. */
    private void MapBundleTooltip()
    {
        if (workspace.Document?.Entry.Reference == SMapRoot.Class) StudioUi.Tip(MapBundleTip);
    }
    /** The Blender add-on is installed by the user from its own repository; Studio ships no copy. */
    public const string BlenderAddonUrl = "https://github.com/stanuwu/SunriseBlenderPlugin";

    /** A link to the add-on, which opening or making these files needs. */
    private static void DrawBlenderAddonLink()
    {
        ImGui.SameLine();
        if (ImGui.Button("Blender add-on")) OpenUrl(BlenderAddonUrl);
        StudioUi.Tip("Needs the Sunrise Blender add-on, installed in Blender separately. Opens its page.");
    }

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    /** Full export follows the asset root regardless of which mesh part is selected. */
    private void ExportFullAsset()
    {
        if (!CanExportBundle) return;
        PickFolder("Empty Blender asset folder", folder =>
        {
            EnsureExternalExport(Path.Combine(folder, "asset.sunrise-bundle.json"));
            workspace.ExportAssetBundle(folder);
        });
    }

    private void ExportWorldScene()
    {
        if (workspace.IsBusy || workspace.World is null) return;
        PickFolder("Empty Blender world folder", folder =>
        {
            EnsureExternalExport(Path.Combine(folder, "world.sunrise-scene.json"));
            workspace.ExportWorldScene(folder);
        });
    }

    private void BeginBundleImport() => PickFile("Sunrise Blender asset|*.sunrise-bundle.json|JSON files|*.json", path =>
    {
        bundlePath = path;
        bundleName = Path.GetFileName(Path.GetDirectoryName(path)) ?? "Imported asset";
        importBundleDialog = true;
    });

    /** A single import mode applies to the complete validated asset group. */
    private void DrawBundleDialogs()
    {
        DrawWorldExportReport();
        DrawSkinImportDialog();
        if (importBundleDialog) { ImGui.OpenPopup("Import Blender asset"); importBundleDialog = false; bundlePackage = workspace.DefaultPackageText; }
        if (observedTransferRevision != workspace.TransferRevision)
        { observedTransferRevision = workspace.TransferRevision; ImGui.OpenPopup("Asset transfer"); }
        CenterModal(520);
        if (ImGui.BeginPopupModal("Import Blender asset", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(Path.GetFileName(Path.GetDirectoryName(bundlePath)));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(bundlePath);
            ImGui.Spacing();
            ImGui.RadioButton("Apply deltas", ref bundleMode, 0);
            ImGui.SameLine(); ImGui.RadioButton("Add new asset", ref bundleMode, 1);
            if (bundleMode == 1)
            {
                ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##bundle-name", "Asset name", ref bundleName, 256);
                ImGui.SetNextItemWidth(130 * scale);
                ImGui.InputText("Package ID", ref bundlePackage, 4, ImGuiInputTextFlags.CharsHexadecimal);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Unused native package ID (hex)");
            }
            bool validPackage = ushort.TryParse(bundlePackage, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort packageId)
                && SunriseStudio.Packages.PackageWriter.IsNewPackageId(packageId);
            ImGui.Spacing();
            ImGui.BeginDisabled(!workspace.CanEdit || bundleMode == 1 && !validPackage);
            if (ImGui.Button("Import", new(110 * scale, 0)))
            {
                var mode = bundleMode == 0 ? AssetBundleImportMode.ApplyDeltas : AssetBundleImportMode.NewAsset;
                workspace.ImportAssetBundle(bundlePath, new(mode, packageId, bundleName));
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button("Cancel", new(110 * scale, 0))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        CenterModal(580);
        if (!ImGui.BeginPopupModal("Asset transfer", ImGuiWindowFlags.AlwaysAutoResize)) return;
        IReadOnlyList<BundleNotice> notices = [];
        if (workspace.LastTransferWasExport && workspace.LastAssetExport is { } exported)
        {
            var manifest = exported.Manifest;
            ImGui.TextUnformatted(manifest.Name);
            ImGui.TextDisabled($"{manifest.Meshes.Count:N0} parts / {manifest.Textures.Count:N0} textures / {manifest.Animations.Count:N0} animations");
            if (ImGui.Button("Open export folder")) OpenFolder(Path.GetDirectoryName(exported.ManifestPath)!);
            DrawBlenderAddonLink();
            notices = manifest.Notices;
        }
        else if (workspace.LastAssetImport is { } imported)
        {
            ImGui.TextUnformatted("Asset imported");
            ImGui.TextDisabled($"{imported.ChangedAssets:N0} changed / {imported.AddedAssets.Count:N0} added");
            notices = imported.Notices;
        }
        if (notices.Count > 0 && ImGui.CollapsingHeader("Compatibility", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var groups = notices.GroupBy(notice => (notice.Surface, notice.Status)).ToArray();
            if (ImGui.BeginTable("transfer-report", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new(540 * scale, Math.Min(220 * scale, (groups.Length + 1) * ImGui.GetTextLineHeightWithSpacing()))))
            {
                ImGui.TableSetupColumn("Asset data"); ImGui.TableSetupColumn("Result"); ImGui.TableHeadersRow();
                foreach (var group in groups)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(group.Key.Surface);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(group.Key.Status);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join('\n', group.Select(notice => notice.Message).Distinct().Take(6)));
                }
                ImGui.EndTable();
            }
        }
        ImGui.Spacing();
        if (ImGui.Button("Close", new(110 * scale, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawWorldExportReport()
    {
        if (workspace.LastWorldExport is not { } result) return;
        if (!ReferenceEquals(observedWorldExport, result))
        { observedWorldExport = result; ImGui.OpenPopup("World export"); }
        CenterModal(520);
        if (!ImGui.BeginPopupModal("World export", ImGuiWindowFlags.AlwaysAutoResize)) return;
        var stats = result.Manifest.Statistics;
        ImGui.TextUnformatted(result.Manifest.Name);
        ImGui.TextDisabled($"{stats.Instances:N0} placements | {stats.Geometries:N0} meshes | {stats.Textures:N0} textures");
        if (result.Manifest.Notices.Any(notice => notice.Status == "Partial source decode"))
            ImGui.TextDisabled("Some source content was unavailable");
        if (ImGui.CollapsingHeader("Notes"))
        {
            var groups = result.Manifest.Notices.GroupBy(notice => (notice.Surface, notice.Status)).ToArray();
            if (ImGui.BeginTable("world-export-notes", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new(0, Math.Min(220 * scale, (groups.Length + 1) * ImGui.GetTextLineHeightWithSpacing()))))
            {
                foreach (var group in groups)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(group.Key.Surface);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{group.Key.Status} ({group.Count():N0})");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(group.First().Message);
                }
                ImGui.EndTable();
            }
        }
        if (ImGui.Button("Open folder")) OpenFolder(Path.GetDirectoryName(result.ManifestPath)!);
        DrawBlenderAddonLink();
        ImGui.SameLine();
        if (ImGui.Button("Close")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
