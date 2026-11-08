using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** A draft refuses changed project, settings, target bytes or another binding edit. */
public sealed class ConfigBindingDraft
{
    private readonly StudioProject project;
    private readonly ModMetadata? metadata;
    private readonly ProjectConfigBinding[] baseline;
    private readonly StudioDocument document;
    private readonly string sourceHash;
    private readonly List<ProjectConfigBinding> values;
    public IReadOnlyList<ConfigBindingCandidate> Sites => document.ConfigSites;
    public IReadOnlyList<ModConfigEntry> Settings => metadata?.Config ?? [];
    public IReadOnlyList<ProjectConfigBinding> Bindings => values.AsReadOnly();
    public uint Tag => document.Entry.TagHash;

    public ConfigBindingDraft(StudioWorkspace workspace)
    {
        project = workspace.Project ?? throw new InvalidOperationException("Create a project first.");
        document = workspace.Document ?? throw new InvalidOperationException("Select a CUI resource.");
        metadata = project.Mod; baseline = project.ConfigBindings.ToArray(); values = baseline.ToList();
        sourceHash = BinaryDelta.Hash(document.Bytes);
    }

    public void Bind(StudioWorkspace workspace, ConfigBindingCandidate site, string key)
    {
        RequireCurrent(workspace);
        var binding = workspace.CreateConfigBinding(site, key);
        values.RemoveAll(value => value.Identity == binding.Identity); values.Add(binding);
    }

    public void Unbind(string identity) => values.RemoveAll(value => value.Identity == identity);

    public void Save(StudioWorkspace workspace)
    {
        RequireCurrent(workspace);
        workspace.SetConfigBindings(values.OrderBy(value => value.Identity, StringComparer.Ordinal).ToArray());
    }

    private void RequireCurrent(StudioWorkspace workspace)
    {
        if (!ReferenceEquals(project, workspace.Project) || !ReferenceEquals(metadata, project.Mod)
            || !project.ConfigBindings.SequenceEqual(baseline) || workspace.Document is not { } current
            || current.Entry.TagHash != document.Entry.TagHash || current.AddedAssetId != document.AddedAssetId
            || BinaryDelta.Hash(current.Bytes) != sourceHash)
            throw new InvalidOperationException("The project, settings or target changed. Reopen the binding editor.");
    }
}

public sealed partial class StudioForm
{
    private ConfigBindingDraft? configBindingDraft;
    private int configSiteIndex = -1;
    private string configSettingKey = "", configBindingError = "", configBindingFilter = "";
    private bool openConfigBindings;

    private void BeginConfigBindings()
    {
        configBindingDraft = new(workspace); configSiteIndex = -1;
        configSettingKey = configBindingError = configBindingFilter = ""; openConfigBindings = true;
    }

    private void DrawConfigBindings()
    {
        if (openConfigBindings) { ImGui.OpenPopup("Config bindings"); openConfigBindings = false; }
        if (!ImGui.BeginPopupModal("Config bindings", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (configBindingDraft is { } draft)
        {
            if (draft.Settings.Count == 0) ImGui.TextWrapped("Add a setting in Build > Mod details first.");
            ImGui.SetNextItemWidth(520 * scale);
            ImGui.InputTextWithHint("##configSiteFilter", "Find scalar property or comparison", ref configBindingFilter, 128);
            StudioUi.Help("Studio shows defaults; the game applies settings after restart.");
            if (ImGui.BeginChild("Config sites", new(600 * scale, 220 * scale), ImGuiChildFlags.Borders))
            {
                for (int i = 0; i < draft.Sites.Count; i++)
                {
                    var site = draft.Sites[i];
                    if (!site.Site.Name.Contains(configBindingFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    var bound = draft.Bindings.FirstOrDefault(value => value.TagHash == draft.Tag && value.Site.Identity == site.Site.Identity);
                    ImGui.PushID(i);
                    if (ImGui.Selectable(site.Site.Name + (bound is null ? "" : "  = " + bound.Key), configSiteIndex == i))
                    { configSiteIndex = i; configSettingKey = bound?.Key ?? ""; }
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
            if ((uint)configSiteIndex < draft.Sites.Count)
            {
                var site = draft.Sites[configSiteIndex];
                var settings = draft.Settings.Where(value => value.Type == site.Type).ToArray();
                ImGui.TextUnformatted(site.Site.Name);
                ImGui.SetNextItemWidth(330 * scale);
                if (ImGui.BeginCombo("Setting", configSettingKey.Length == 0 ? "Choose " + site.Type.ToString().ToLowerInvariant() + " setting" : configSettingKey))
                {
                    foreach (var setting in settings)
                        if (ImGui.Selectable($"{setting.Key} = {setting.Default}", configSettingKey == setting.Key)) configSettingKey = setting.Key;
                    ImGui.EndCombo();
                }
                ImGui.BeginDisabled(!settings.Any(value => value.Key == configSettingKey));
                if (ImGui.Button("Bind setting"))
                {
                    try { draft.Bind(workspace, site, configSettingKey); configBindingError = ""; }
                    catch (Exception error) when (BindingFailure(error)) { configBindingError = error.Message; }
                }
                ImGui.EndDisabled();
                if (settings.FirstOrDefault(value => value.Key == configSettingKey) is { } chosenSetting)
                    ImGui.TextDisabled($"Default: {chosenSetting.Default}");
                if (site.Site.Profile == ConfigBindingLimits.PropertyProfile && site.Type == ModConfigType.Float)
                    ImGui.TextDisabled(site.Site.PoolOffset == CuiDocument.FloatsB ? "Units: fraction of screen height" : "Units: unscaled native value");
            }
            var boundRows = draft.Bindings.Where(value => value.TagHash == draft.Tag).ToArray();
            if (boundRows.Length != 0 && ImGui.BeginChild("Bound settings", new(600 * scale, Math.Clamp(boundRows.Length * 26, 40, 150) * scale)))
            {
                foreach (var binding in boundRows)
                {
                    ImGui.PushID(binding.Identity); ImGui.TextUnformatted($"{binding.Site.Name}: {binding.Key}"); ImGui.SameLine();
                    if (ImGui.SmallButton("Unbind")) draft.Unbind(binding.Identity);
                    ImGui.PopID();
                }
            }
            if (boundRows.Length != 0) ImGui.EndChild();
            if (configBindingError.Length != 0) ImGui.TextWrapped(configBindingError);
            ImGui.BeginDisabled(!workspace.CanEdit);
            if (ImGui.Button("Save bindings"))
            {
                try { draft.Save(workspace); configBindingDraft = null; ImGui.CloseCurrentPopup(); }
                catch (Exception error) when (BindingFailure(error)) { configBindingError = error.Message; }
            }
            ImGui.EndDisabled(); ImGui.SameLine();
        }
        if (ImGui.Button("Cancel")) { configBindingDraft = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private static bool BindingFailure(Exception error) => error is InvalidDataException or InvalidOperationException
        or IOException or UnauthorizedAccessException or KeyNotFoundException or ArgumentException or OverflowException;
}
