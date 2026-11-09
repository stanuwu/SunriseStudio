/** File: Data behind the Help window and the get_help MCP tool: keybind rows from the shortcut table plus topic tips. */
namespace SunriseStudio.Viewport;

/** One keybind row: area, what it does, its current keys (the user's map), and whether Edit > Shortcuts rebinds it. */
public sealed record HelpKey(string Area, string Action, string Keys, string Id, bool Rebindable);

/** One help tip: topic, title and body text. */
public sealed record HelpTip(string Topic, string Title, string Text);

/**
 * Help > Keybinds & Tips (F1) and the get_help MCP tool. Keybinds come from ShortcutActions.All and
 * the user's ShortcutMap at call time, so a new or rebound action shows up without touching this file;
 * an action's area comes from its id (AreaOf). FixedInputs lists the keys the viewports (fly and player
 * movement) read directly, which no preset can rebind. The AI terminal's keys went with the in-editor console
 * (docs/UI-UE5.md §4.3); the AI runs in the separate Solforge AI app now.
 */
public static class HelpCatalog
{
    public const string General = "General", Viewport = "Viewport", Hierarchy = "Hierarchy", Inspector = "Inspector",
        Graphs = "Mission & node graphs", Ai = "AI / MCP", Live = "Live";

    public static IReadOnlyList<string> Areas { get; } = [General, Viewport, Hierarchy, Inspector, Graphs, Ai, Live];

    /** Maps shortcut action ids to their help area heading. */
    private static readonly Dictionary<string, string> AreaById = new(StringComparer.Ordinal)
    {
        [ShortcutActions.OpenAiWorkspace] = Ai,
        [ShortcutActions.UpdateRunningGame] = Live,
        [ShortcutActions.ToggleViewportRendering] = Viewport,
        [ShortcutActions.ToggleTree3D] = Hierarchy,
        [ShortcutActions.HideSelection3D] = Hierarchy,
        [ShortcutActions.ShowAll3D] = Hierarchy,
        [ShortcutActions.NavigateBack] = Inspector,
        [ShortcutActions.NavigateForward] = Inspector,
        [ShortcutActions.ToggleBrowser] = Inspector,
    };

    /** The area an action belongs to: explicit rows first, then its id prefix, then its section. */
    public static string AreaOf(ShortcutAction action)
    {
        if (AreaById.TryGetValue(action.Id, out string? area)) return area;
        string id = action.Id;
        if (id.StartsWith("graph", StringComparison.Ordinal) || id.StartsWith("node", StringComparison.Ordinal)
            || id.StartsWith("mission", StringComparison.Ordinal) || id.Contains(".graph", StringComparison.Ordinal)) return Graphs;
        if (id.StartsWith("ai.", StringComparison.Ordinal) || id.StartsWith("mcp.", StringComparison.Ordinal)) return Ai;
        if (id.StartsWith("live.", StringComparison.Ordinal)) return Live;
        if (id.StartsWith("hierarchy.", StringComparison.Ordinal) || id.StartsWith("tree.", StringComparison.Ordinal)) return Hierarchy;
        if (id.StartsWith("inspector.", StringComparison.Ordinal)) return Inspector;
        return action.Section switch { ShortcutSection.View3D => Viewport, ShortcutSection.View2D => Graphs, _ => General };
    }

    /** Keys the viewports read directly; listed here so Help shows them. */
    public static IReadOnlyList<HelpKey> FixedInputs { get; } =
    [
        new(Viewport, "Fly: forward, left, back, right (hold Right mouse)", "W A S D", "fixed.fly_move", false),
        new(Viewport, "Fly: down, up (hold Right mouse)", "Q / E", "fixed.fly_vertical", false),
        new(Viewport, "Fly or walk faster", "Shift (held)", "fixed.sprint", false),
        new(Viewport, "Fly or walk speed", "Wheel while flying or in the player camera", "fixed.speed", false),
        new(Viewport, "Player camera: walk", "W A S D", "fixed.player_move", false),
        new(Viewport, "Player camera: rise, sink", "Space / Ctrl", "fixed.player_vertical", false),
        new(Viewport, "Perspective or orthographic", "Numpad 5", "fixed.ortho", false),
        new(General, "Cancel a shortcut capture or close a dialog", "Escape", "fixed.escape", false),
    ];

    /** Every keybind with the user's current keys, grouped in Areas order. */
    public static IReadOnlyList<HelpKey> Keys(ShortcutMap map) =>
        ShortcutActions.All.Select(action => new HelpKey(AreaOf(action), action.Name, map.Text(action.Id) is { Length: > 0 } keys ? keys : "Unbound",
                action.Id, true))
            .Concat(FixedInputs)
            .OrderBy(key => AreaIndex(key.Area)).ToArray();

    /** Rows whose area, action, keys or id contain every word of the query. */
    public static IReadOnlyList<HelpKey> Search(ShortcutMap map, string? query, string? area = null) =>
        Keys(map).Where(key => (string.IsNullOrEmpty(area) || string.Equals(key.Area, area, StringComparison.OrdinalIgnoreCase))
            && Matches(query, key.Area + " " + key.Action + " " + key.Keys + " " + key.Id)).ToArray();

    /** Tips matching every word of the query. */
    public static IReadOnlyList<HelpTip> SearchTips(string? query) =>
        Tips.Where(tip => Matches(query, tip.Topic + " " + tip.Title + " " + tip.Text)).ToArray();

    /** Whether text contains every space-separated word of the query (an empty query matches). */
    public static bool Matches(string? query, string text) => string.IsNullOrWhiteSpace(query)
        || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    /** Sort position of an area name; unknown areas sort last. */
    private static int AreaIndex(string area)
    {
        for (int i = 0; i < Areas.Count; i++) if (Areas[i] == area) return i;
        return Areas.Count;
    }

    public static IReadOnlyList<HelpTip> Tips { get; } =
    [
        new("Workflow", "Projects never touch the game", "Edits live in the project (sunrise.project.json and content/). Build packages, then Build and install " +
            "writes the mod to Sunrise\\mods beside destiny2.exe when that folder exists, else bin\\x64\\Sunrise\\mods; Changes lists every edit with undo and Revert."),
        new("Workflow", "Find anything by name", "The Asset browser (Ctrl+Space in the Sunrise preset) searches the whole install; Back/Forward " +
            "walk your inspection history like a browser."),
        new("Workflow", "Clone, then edit", "File > New/Clone makes weapons, armor, plugs, subclasses and more with fresh hashes; edit the clone in the " +
            "inspector. Clone subclass gives a new subclass item; Window > Content editors shapes its nodes."),
        new("Workflow", "Content editors", "Window > Content editors holds subclasses, abilities, damage types, loot pools, enemies, activities and the " +
            "seasonal artifact. Each family says whether the game reads it (Live) or it is authoring only. Export writes build/server/."),
        new("Workflow", "Loot pools go live", "Export writes build/server/scripts/loot_pools.lua; copy it to Sunrise\\scripts beside the server. The server " +
            "re-reads it when it changes; weight 0 removes an item from a built-in pool."),
        new("Workflow", "Subclass names go live", "Export writes build/server/data/subclass_nodes.tsv; copy it to Sunrise\\data. Rows win over the server's " +
            "built-in node names, keyed by the subclass item hash."),
        new("Workflow", "Rebind anything", "Edit > Shortcuts chooses a preset (Sunrise, Blender, Unity, Maya, Unreal) and rebinds actions; this page " +
            "always shows your current keys."),
        new("Performance", "Turn the 3D viewport off", "Ctrl+Shift+R (Sunrise preset) stops all 3D drawing; panels, inspectors and every AI tool " +
            "except screenshots keep working."),
        new("Performance", "Low memory mode", "Settings > Low memory mode (16 GB PCs) keeps Solforge lighter while the game runs alongside it."),
        new("Performance", "Texture budget", "Settings > Texture budget (MiB) caps GPU texture memory; lower it when large maps stutter."),
        new("Performance", "Frame caps", "Settings > FPS cap, focused and FPS cap, background; a background cap keeps the game smooth while " +
            "Solforge stays open."),
        new("Performance", "Disk cache", "File > Cache... shows and clears decoded asset caches when the install changes."),
        new("MCP", "What the MCP server is", "Solforge runs a local MCP server from the moment it starts (Settings > AI switches it off). Any MCP " +
            "client can read the open map, assets, items, content documents and help, and ask for actions that wait for your Approve click. " +
            "With Solforge closed, solforge-mcp.exe --sol still answers the tools that need no editor; editor tools attach when Solforge starts."),
        new("MCP", "Solforge AI", "The toolbar's Solforge AI button (Ctrl+Shift+A) launches the separate Solforge AI app for the open project, or " +
            "brings it to the front; its arrow menu stops it, re-checks it and opens its settings. Requests waiting for approval show as a badge " +
            "on the button and in the Solforge AI panel (Window > Solforge AI panel); MCP tools can never launch or stop the app."),
        new("MCP", "Claude Code, Cline, Antigravity", "Solforge writes one 'sol' MCP entry for Claude Code (.mcp.json in the project and " +
            "~/.claude.json), Cline, Antigravity and the Pi harness. sol aggregates the editor, sol-testkit, tiger-mcp and the delegates; " +
            "it runs solforge-mcp.exe --sol, and no token goes into a config file."),
        new("MCP", "Other AI clients", "Any stdio MCP client works: command = solforge-mcp.exe (next to Solforge.exe), args = [\"--sol\", " +
            "\"--project\", \"<project folder>\", \"--upstreams\", \"<local sol-upstreams file>\"]. Without --upstreams it exposes Solforge only. " +
            "Start with get_editor_state, get_help and list_content_families."),
        new("MCP", "Pausing context", "Publish context in the Solforge AI panel stops publishing what you look at; catalog, content and help tools keep working."),
        new("MCP", "Content tools", "list_content_families, list_content, get_content, validate_content and check_subclass_build read; create_content, " +
            "update_content, delete_content and export_content change the project after you approve."),
        new("Live", "Update the running game", "Build > Update running game replaces changed scripts in the installed mod and reloads them in the running game."),
        new("Live", "Never install while the game runs", "Close Destiny 2 before Build and install. Each install first copies the previous mod folder to <project>/.solforge/snapshots for rollback."),
    ];
}
