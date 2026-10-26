using System.Numerics;
using ImGuiNET;
using SunriseStudio.Packages;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Solforge additions kept in their own file so upstream merges of StudioForm stay small. */
public sealed partial class StudioForm
{
    /** Upstream credit shown under Help and on the toolbar version tooltip. */
    private const string AboutCredit = "Built on Sunrise Studio by stan (github.com/stanuwu) and its contributors";

    private string statusFacts = "", statusFactsTip = "";
    private long statusFactsTicks;
    private PackageCatalog? statusKeysCatalog;
    private string statusKeys = "";
    private string? statusFolder;
    private bool statusFolderExists;

    /**
     * Right-aligned profile, package/key/cache health and memory (recomputed at most once a second), then the MCP fact:
     * a status dot and the call count of the editor's MCP server; clicking it opens the Solforge AI panel.
     */
    private void DrawStatusFacts()
    {
        long now = Environment.TickCount64;
        if (statusFacts.Length == 0 || now - statusFactsTicks >= 1000) { statusFactsTicks = now; RefreshStatusFacts(); }
        string mcpStatus = "MCP off";
        AiServiceStatus(ref mcpStatus);
        var (mcpText, mcpColor) = McpFact(mcpStatus);
        float line = ImGui.GetTextLineHeight();
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float mcpWidth = line + ImGui.CalcTextSize(mcpText).X;
        float width = ImGui.CalcTextSize(statusFacts).X + spacing * 3 + mcpWidth;
        ImGui.SameLine();
        float x = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (x > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(x);
        ImGui.TextDisabled(statusFacts);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(statusFactsTip);
        ImGui.SameLine(0, spacing);
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        draw.AddLine(at + new Vector2(0, line * .1f), at + new Vector2(0, line * .9f), SolTheme.U32(SolTheme.BorderHi), 1);
        ImGui.SetCursorScreenPos(at + new Vector2(spacing * 2, 0));
        var start = ImGui.GetCursorScreenPos();
        if (UiAuto.InvisibleButton("status-mcp", "MCP", new(mcpWidth, line))) OpenAiWorkspace();
        bool hovered = ImGui.IsItemHovered();
        UiMotion.Dot(draw, start + new Vector2(line * .4f, line * .5f), line * .22f, ImGui.GetItemID(), mcpColor);
        draw.AddText(start + new Vector2(line, 0), SolTheme.U32(hovered ? SolTheme.TextStrong : SolTheme.Muted), mcpText);
        if (hovered) ImGui.SetTooltip(mcpStatus + "\nClick to open the Solforge AI panel.");
    }

    /** The MCP fact's short text and dot colour from the AiServiceStatus line. */
    private static (string Text, Vector4 Color) McpFact(string status)
    {
        if (status.StartsWith("MCP :", StringComparison.Ordinal))
        {
            int calls = status.IndexOf(" calls", StringComparison.Ordinal);
            int space = calls > 0 ? status.LastIndexOf(' ', calls - 1) : -1;
            string count = space > 0 ? status[(space + 1)..calls] : "";
            return (count.Length > 0 ? $"MCP {count}" : "MCP", status.Contains("(paused)", StringComparison.Ordinal) ? SolTheme.Warn : SolTheme.Ok);
        }
        return status.StartsWith("MCP error", StringComparison.Ordinal) ? ("MCP error", SolTheme.Bad) : ("MCP off", SolTheme.Dim);
    }

    private void RefreshStatusFacts()
    {
        var catalog = workspace.Catalog;
        // The key source only changes with the installation, so the file check runs once per catalog.
        if (!ReferenceEquals(catalog, statusKeysCatalog))
        {
            statusKeysCatalog = catalog;
            statusKeys = catalog is null ? "" : File.Exists(Path.Combine(catalog.GameRoot, "bin", "x64", "Sunrise",
                "exports", "package_keys.bin")) ? "keys: export" : "keys: exe scan";
        }
        string packages = catalog is null ? "packages: not loaded" : $"packages: {catalog.PackageCount}";
        string cache = catalog is null ? "" : catalog.DiskCache is { Enabled: true }
            ? $"cache: {workspace.Names?.CacheStatus ?? "Not loaded"}" : "cache: off";
        long managed = GC.GetTotalMemory(false) >> 20, working = Environment.WorkingSet >> 20;
        statusFacts = string.Join("  |  ", new[] { "D2 86657", packages, statusKeys, cache, $"mem: {working} MB" }
            .Where(part => part.Length != 0));
        statusFactsTip = "Profile: D2_ARRIVALS_86657 (Destiny 2 build 86657)\n"
            + (catalog is null ? "No installation loaded.\n" : $"Installation: {catalog.GameRoot}\n")
            + "Keys: 'export' reads bin\\x64\\Sunrise\\exports\\package_keys.bin; 'exe scan' falls back to destiny2.exe.\n"
            + $"Memory: {working} MB working set, {managed} MB managed heap.";
    }

    /** Directory.Exists runs once per notice folder, not every frame. */
    private bool StatusFolderExists(string folder)
    {
        if (folder != statusFolder) { statusFolder = folder; statusFolderExists = Directory.Exists(folder); }
        return statusFolderExists;
    }
}
