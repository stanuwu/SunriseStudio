using System.Globalization;
using ImGuiNET;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private bool modDialog, modCheat;
    private string modId = "", modVersion = "", modName = "", modDescription = "", modAuthors = "", modSource = "",
        modIncompatible = "";
    private readonly List<string[]> modDependencies = [];
    private readonly List<ModConfigRow> modConfig = [];
    // Order matches ModConfigType.
    private static readonly string[] ModConfigTypes = ["int", "float", "bool"];

    /** Each type keeps its own default, so switching the type back restores the earlier value. */
    private sealed class ModConfigRow
    {
        public string Key = "", Name = "";
        public int Type, Integer;
        public float Real;
        public bool Flag;

        public string Default => (ModConfigType)Type switch
        {
            ModConfigType.Int => Integer.ToString(CultureInfo.InvariantCulture),
            ModConfigType.Float => Real.ToString(CultureInfo.InvariantCulture),
            _ => Flag ? "true" : "false"
        };
    }

    /** A project without mod details starts from its own name and version 1.0.0. */
    private void OpenModDialog()
    {
        if (workspace.Project is not { } project) return;
        var mod = project.Mod;
        modId = mod?.Id ?? ""; modVersion = mod?.Version ?? "1.0.0"; modName = mod?.Name ?? project.Name;
        modDescription = mod?.Description ?? ""; modSource = mod?.Src ?? "";
        modAuthors = string.Join('\n', mod?.Authors ?? []);
        modIncompatible = string.Join('\n', mod?.Incompatible ?? []);
        modDependencies.Clear();
        foreach (var dependency in mod?.Dependencies ?? [])
            modDependencies.Add([dependency.Id, dependency.MinVersion, dependency.MaxVersion ?? ""]);
        modCheat = mod?.Cheat ?? false;
        modConfig.Clear();
        foreach (var entry in mod?.Config ?? [])
        {
            var row = new ModConfigRow { Key = entry.Key, Name = entry.Name ?? "", Type = (int)entry.Type };
            ModManifest.TryParseInt(entry.Default, out row.Integer);
            ModManifest.TryParseFloat(entry.Default, out row.Real);
            ModManifest.TryParseBool(entry.Default, out row.Flag);
            modConfig.Add(row);
        }
        modDialog = true;
    }

    /** Install reads saved mission scripts, so an unsaved draft is saved first or left out. */
    private void InstallMod()
    {
        if (SettleModCommand("Build and install", "installing")) workspace.Run(workspace.BuildAndInstall);
    }

    /** Writes changed packages and scripts into the installed mod and loads them in the running game. */
    public void UpdateRunningGame()
    {
        if (SettleModCommand("Update running game", "updating")) workspace.Run(() => workspace.UpdateRunningGame());
    }

    /** Returns false when the mod details are unset or the user keeps an unsaved mission draft open. */
    private bool SettleModCommand(string title, string action)
    {
        if (workspace.Project?.Mod is null)
        {
            OpenModDialog();
            workspace.Error = $"Set the mod details before {action}.";
            return false;
        }
        if (!missionGraph.IsDirty || options.Smoke) return true;
        bool previous = nativeDialog;
        nativeDialog = true;
        try
        {
            var answer = MessageBox.Show(this, $"Save mission changes before {action}?", title,
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes)
            {
                missionGraph.SaveCurrent(workspace);
                if (missionGraph.IsDirty) { focusMissionGraph = true; return false; }
            }
        }
        finally { nativeDialog = previous; }
        return true;
    }

    private void DrawModDialog()
    {
        CenterModal(620);
        if (!ImGui.BeginPopupModal("Mod details", ImGuiWindowFlags.AlwaysAutoResize)) return;
        float wide = 600 * scale;
        ImGui.TextUnformatted("Id"); ImGui.SetNextItemWidth(wide);
        ImGui.InputTextWithHint("##modId", "dev.example.mod", ref modId, 256);
        ImGui.TextUnformatted("Version"); ImGui.SetNextItemWidth(160 * scale);
        ImGui.InputTextWithHint("##modVersion", "1.0.0", ref modVersion, 64);
        ImGui.TextUnformatted("Name"); ImGui.SetNextItemWidth(wide);
        ImGui.InputText("##modName", ref modName, 256);
        ImGui.TextUnformatted("Description");
        ImGui.InputTextMultiline("##modDescription", ref modDescription, 2048, new(wide, 70 * scale));
        ImGui.TextUnformatted("Authors, one per line");
        ImGui.InputTextMultiline("##modAuthors", ref modAuthors, 2048, new(wide, 55 * scale));
        ImGui.TextUnformatted("Source URL"); ImGui.SetNextItemWidth(wide);
        ImGui.InputText("##modSource", ref modSource, 1024);
        ImGui.TextUnformatted("Dependencies");
        for (int index = 0; index < modDependencies.Count; index++)
        {
            var row = modDependencies[index];
            ImGui.PushID(index);
            ImGui.SetNextItemWidth(300 * scale); ImGui.InputTextWithHint("##id", "Mod id", ref row[0], 256);
            ImGui.SameLine(); ImGui.SetNextItemWidth(100 * scale); ImGui.InputTextWithHint("##min", "Minimum", ref row[1], 64);
            ImGui.SameLine(); ImGui.SetNextItemWidth(100 * scale); ImGui.InputTextWithHint("##max", "Maximum", ref row[2], 64);
            ImGui.SameLine();
            bool remove = ImGui.SmallButton("Remove");
            ImGui.PopID();
            if (remove) { modDependencies.RemoveAt(index); break; }
        }
        if (ImGui.SmallButton("Add dependency")) modDependencies.Add(["", "1.0.0", ""]);
        ImGui.TextUnformatted("Incompatible mod ids, one per line");
        ImGui.InputTextMultiline("##modIncompatible", ref modIncompatible, 4096, new(wide, 55 * scale));
        ImGui.Checkbox("Cheat", ref modCheat);
        StudioUi.Tip("No progress saves while this mod is loaded.");
        DrawModConfig();
        var metadata = CurrentMod();
        var problems = ModManifest.Validate(metadata);
        foreach (string problem in problems) ImGui.TextColored(StudioTheme.Accent, problem);
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        ImGui.Spacing();
        ImGui.BeginDisabled(problems.Count > 0 || !workspace.CanEdit);
        if (ImGui.Button("Save", new(120 * scale, 0))) workspace.Run(() =>
        {
            workspace.SetModMetadata(metadata);
            ImGui.CloseCurrentPopup();
        });
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new(120 * scale, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /** A row is removed after the table ends, so row ids stay stable while it draws. */
    private void DrawModConfig()
    {
        ImGui.TextUnformatted("Config");
        if (modConfig.Count > 0 && ImGui.BeginTable("##modConfig", 5, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 160 * scale);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 80 * scale);
            ImGui.TableSetupColumn("Default", ImGuiTableColumnFlags.WidthFixed, 120 * scale);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 160 * scale);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60 * scale);
            ImGui.TableHeadersRow();
            int removed = -1;
            for (int index = 0; index < modConfig.Count; index++)
            {
                var row = modConfig[index];
                ImGui.PushID(index); ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##key", "key", ref row.Key, 64);
                ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                ImGui.Combo("##type", ref row.Type, ModConfigTypes, ModConfigTypes.Length);
                ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                switch ((ModConfigType)row.Type)
                {
                    case ModConfigType.Int: ImGui.InputInt("##default", ref row.Integer, 0); break;
                    case ModConfigType.Float: ImGui.InputFloat("##default", ref row.Real, 0, 0, "%g"); break;
                    default: ImGui.Checkbox("##default", ref row.Flag); break;
                }
                ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##name", "Name", ref row.Name, 256);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Remove")) removed = index;
                ImGui.PopID();
            }
            ImGui.EndTable();
            if (removed >= 0) modConfig.RemoveAt(removed);
        }
        if (ImGui.SmallButton("Add config entry")) modConfig.Add(new());
    }

    /** Blank optional fields and list lines are dropped so they are left out of mod.json. */
    private ModMetadata CurrentMod()
    {
        var dependencies = modDependencies.Select(row => new ModDependency(row[0].Trim(), row[1].Trim(), Optional(row[2])))
            .ToArray();
        var config = modConfig.Select(row => new ModConfigEntry(row.Key.Trim(), (ModConfigType)row.Type, row.Default,
            Optional(row.Name))).ToArray();
        return new(modId.Trim(), modVersion.Trim(), modName.Trim(), Optional(modDescription), Lines(modAuthors),
            Optional(modSource), dependencies.Length == 0 ? null : dependencies, Lines(modIncompatible), modCheat,
            config.Length == 0 ? null : config);
    }

    private static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static IReadOnlyList<string>? Lines(string text)
    {
        string[] lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToArray();
        return lines.Length == 0 ? null : lines;
    }
}
