using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using Solforge.Weapons;
using SunriseStudio.Authoring;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using McpPages = Solforge.Mcp.Pages;

namespace SunriseStudio.Editor;

/**
 * The project-wide MCP reads and actions (SolforgeTools.Project.cs). Small reads run on the UI thread; anything that walks
 * the install, the map or the item catalog takes its references on the UI thread and pages off it, so a call never stalls
 * the editor and works while the 3D viewport is off. Nothing here needs a map open unless the tool says so.
 */
public sealed partial class StudioForm
{
    /** Builds an error result object. */
    private static JsonObject AiError(string message) => new() { ["error"] = message };
    /** Formats a tag hash as 0x%08X. */
    private static string Hex(uint value) => $"0x{value:X8}";
    /** The paging offset argument (never negative). */
    private static int Offset(JsonObject args) => Math.Max(0, McpJson.Int(args, "offset", 0));
    /** The paging limit argument, clamped to 1 and the maximum page size. */
    private static int Limit(JsonObject args) => Math.Clamp(McpJson.Int(args, "limit", 50), 1, McpPages.MaximumLimit);

    /** Every word of the query appears in the text. */
    private static bool AllWords(string? query, string text) => query is not { Length: > 0 }
        || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    /** Cached map and mission rows with the catalog they came from. */
    private (WorldCatalog? Catalog, JsonObject[] Maps, JsonObject[] Missions)? aiWorldRows;

    /** Dispatches a project query tool by name (SolforgeTools.Project.cs defines the tools). */
    private async Task<JsonObject> AiQueryAsync(string tool, JsonObject args, CancellationToken cancel)
    {
        try
        {
            return tool switch
            {
                "get_editor_state" => await OnUi(AiEditorState).WaitAsync(cancel),
                "get_project" => await OnUi(AiProject).WaitAsync(cancel),
                "get_build_status" => await OnUi(AiBuildStatus).WaitAsync(cancel),
                "get_settings" => await OnUi(AiSettings).WaitAsync(cancel),
                "list_project_files" => await AiOffThread(() => workspace.Project?.DirectoryPath, root => AiProjectFiles(root, args), cancel),
                "list_project_changes" => await OnUi(() => AiProjectChanges(args)).WaitAsync(cancel),
                "list_maps" or "list_missions" => await AiWorldListAsync(tool, args, cancel),
                "list_packages" => await AiOffThread(() => workspace.Catalog, catalog => AiPackages(catalog, args), cancel),
                "list_package_entries" => await AiOffThread(() => (workspace.Catalog, workspace.Names), pair => AiPackageEntries(pair.Catalog, pair.Names, args), cancel),
                "search_assets" => await AiOffThread(() => workspace.Assets, assets => AiSearchAssets(assets, args), cancel),
                "list_mission_scripts" => await AiOffThread(() => (workspace.Project?.DirectoryPath, settings.ScriptsPath), roots => AiMissionScripts(roots.Item1, roots.ScriptsPath, args), cancel),
                "find_world_objects" => await AiOffThread(() => (inspectionWorld, AiCaptureNow()), pair => AiFindWorldObjects(pair.inspectionWorld, pair.Item2, args), cancel),
                "get_placement" => await OnUi(() => AiPlacement(McpJson.String(args, "id") ?? "")).WaitAsync(cancel),
                "search_items" or "get_item" or "search_perks" or "list_vendors" or "get_vendor" => await AiInvestmentAsync(tool, args, cancel),
                "list_shaders" or "get_shader" or "list_animations" or "get_animation" or "list_audio" or "get_audio" => await AiAssetAsync(tool, args, cancel),
                "list_creation_types" => AiCreationTypes(McpJson.String(args, "filter")),
                "compare_with_base" or "get_item_sources" => await AiItemSourceQueryAsync(tool, args, cancel),
                "get_help" => await OnUi(() => AiHelp(args)).WaitAsync(cancel),
                "list_commands" => await OnUi(AiCommandStates).WaitAsync(cancel),
                "get_player_position" => await AiPlayerPositionAsync(args, cancel),
                "check_mission_script" or "simulate_mission" => await AiMissionHostAsync(tool, args, cancel),
                "describe_creation" => await OnUi(() => AiDescribeCreation(McpJson.String(args, "key") ?? "", McpJson.String(args, "mode") ?? "")).WaitAsync(cancel),
                _ => AiError("Unknown tool.")
            };
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException or IOException
            or UnauthorizedAccessException or KeyNotFoundException or NotSupportedException or TimeoutException)
        {
            return AiError(error.Message);
        }
    }

    /** Takes references on the UI thread, then does the paging on a worker. */
    private async Task<JsonObject> AiOffThread<T>(Func<T> capture, Func<T, JsonObject> read, CancellationToken cancel)
    {
        var taken = await OnUi(capture).WaitAsync(cancel).ConfigureAwait(false);
        return await Task.Run(() => read(taken), cancel).WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
    }

    /** get_editor_state: installation, game, open pages and so on. */
    private JsonObject AiEditorState()
    {
        bool d2 = destiny2Detected && settings.PauseRenderWhenGameRunning && !livePage.IsAttached && !(viewport?.ForceResumeRendering ?? false);
        string rendering = !settings.ViewportRendering ? "off (user)" : d2 ? "paused while Destiny 2 runs" : "on";
        return new JsonObject
        {
            ["installation"] = workspace.Catalog?.GameRoot ?? (settings.SunrisePath.Length > 0 ? settings.SunrisePath : null),
            ["packages"] = workspace.Catalog?.PackageCount,
            ["mapsIndexed"] = workspace.WorldCatalog is not null,
            ["project"] = workspace.Project is { } project ? new JsonObject { ["name"] = project.Name, ["folder"] = project.DirectoryPath } : null,
            ["map"] = workspace.World is { } world ? new JsonObject { ["name"] = world.Map.Name, ["tag"] = Hex(world.Map.Tag),
                ["mission"] = world.MissionTag is { } mission ? Hex(mission) : null, ["objects"] = world.Objects.Count } : null,
            ["asset"] = workspace.Document is { } document ? new JsonObject { ["name"] = document.Name, ["tag"] = Hex(document.Entry.TagHash) } : null,
            ["missionScript"] = missionGraph.ScriptPath is { } script ? AiShownPath(script) : null,
            ["busy"] = workspace.IsBusy, ["status"] = workspace.Status, ["progress"] = workspace.BackgroundStatus,
            ["error"] = workspace.Error.Length > 0 ? workspace.Error : null,
            ["notice"] = workspace.Notice?.Text,
            ["viewportRendering"] = rendering, ["viewportQuality"] = settings.ViewportQuality.ToString(),
            ["liveAttached"] = livePage.IsAttached, ["destiny2Running"] = destiny2Detected,
            ["itemCatalogLoaded"] = workspace.Investment is not null,
            ["skillsFolder"] = SkillSync.Folder,
            ["skills"] = new JsonArray(SkillSync.Names(SkillSync.Folder).Select(name => (JsonNode)name).ToArray()),
            ["memory"] = AiMemoryBreakdown(),
            ["automatic"] = AiAutomaticJson()
        };
    }

    /** get_project: the open project facts. */
    private JsonObject AiProject()
    {
        if (workspace.Project is not { } project) return AiError("No project is open; open_project or create_project opens one (File > Open project).");
        return new JsonObject
        {
            ["name"] = project.Name, ["folder"] = project.DirectoryPath,
            ["mod"] = project.Mod is { } mod ? new JsonObject
            {
                ["id"] = mod.Id, ["name"] = mod.Name, ["version"] = mod.Version, ["description"] = mod.Description,
                ["authors"] = mod.Authors is { } authors ? new JsonArray(authors.Select(author => (JsonNode)author).ToArray()) : null,
                ["source"] = mod.Src, ["cheat"] = mod.Cheat
            } : null,
            ["modNote"] = project.Mod is null ? "No mod details; Build and install, Update running game and .solmod packing need them (set_mod_details)." : null,
            ["assetChanges"] = project.Changes.Count, ["objectEdits"] = project.ObjectEdits.Count, ["addedAssets"] = project.AddedAssets.Count,
            ["installation"] = workspace.Catalog?.GameRoot,
            ["missionScriptUnsaved"] = missionGraph.IsDirty
        };
    }

    /** get_build_status: state of the last project build. */
    private JsonObject AiBuildStatus()
    {
        var build = workspace.LastBuild;
        var install = workspace.LastInstall;
        return new JsonObject
        {
            ["busy"] = workspace.IsBusy, ["status"] = workspace.Status, ["progress"] = workspace.BackgroundStatus,
            ["error"] = workspace.Error.Length > 0 ? workspace.Error : null,
            ["notice"] = workspace.Notice is { } notice ? new JsonObject { ["text"] = notice.Text, ["failed"] = notice.Failed, ["details"] = notice.Details } : null,
            ["lastBuild"] = build is null ? null : new JsonObject
            {
                ["output"] = build.OutputDirectory, ["packages"] = build.PackageFiles.Count, ["addedAssets"] = build.AddedAssets.Count,
                ["objectEdits"] = build.ObjectEdits?.Count ?? 0, ["notes"] = new JsonArray(build.Notes.Take(50).Select(note => (JsonNode)note).ToArray())
            },
            ["lastInstall"] = install is null ? null : new JsonObject
            {
                ["modFolder"] = install.ModDirectory, ["packages"] = install.PackageFiles, ["patches"] = install.PatchFiles, ["scripts"] = install.ScriptFiles
            }
        };
    }

    /** get_settings: the editor settings the AI may read. */
    private JsonObject AiSettings() => new()
    {
        ["installation"] = workspace.Catalog?.GameRoot ?? settings.SunrisePath,
        ["project"] = workspace.Project?.DirectoryPath,
        ["serverScripts"] = settings.ScriptsPath.Length > 0 ? settings.ScriptsPath : null,
        ["blender"] = settings.BlenderPath.Length > 0 ? settings.BlenderPath : null,
        ["viewportRendering"] = settings.ViewportRendering, ["pauseWhileGameRuns"] = settings.PauseRenderWhenGameRunning,
        ["lowMemoryMode"] = settings.LowMemoryMode, ["lowMemoryWithGame"] = settings.LowMemoryWithGame, ["textureBudgetMiB"] = settings.TextureBudgetMiB,
        ["textureCacheMiB"] = settings.TextureCacheMiB,
        ["fpsFocused"] = settings.FpsFocused, ["fpsBackground"] = settings.FpsBackground, ["uiScale"] = Math.Round(settings.UiScale, 2),
        ["memory"] = AiMemory(),
        ["aiEngine"] = AiEngines[aiEngine].Name, ["aiAutoAllow"] = settings.AiAutoAllow, ["mcp"] = settings.AiMcpEnabled,
        ["aiFolders"] = new JsonArray(settings.AiFolders.Select(folder => (JsonNode)folder).ToArray())
    };

    /** Where RAM goes, in MiB: process, managed heap, and the caches that only speed up reloads. */
    private JsonObject AiMemory()
    {
        /** Rounds a byte count to MiB with one decimal. */
        static double MiB(long bytes) => Math.Round(bytes / 1048576.0, 1);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        var plan = workspace.NativePlan;
        return new()
        {
            ["workingSet"] = MiB(process.WorkingSet64), ["privateBytes"] = MiB(process.PrivateMemorySize64),
            ["managedHeap"] = MiB(GC.GetTotalMemory(false)), ["managedCommitted"] = MiB(gc.TotalCommittedBytes),
            ["textureCache"] = MiB(workspace.TextureCacheBytes), ["packageBlockCache"] = MiB(workspace.Catalog?.CachedBlockBytes ?? 0),
            ["planGeometry"] = plan is null ? null : (JsonNode)MiB(plan.GeometryBytes), ["planTexturesGpu"] = plan is null ? null : (JsonNode)MiB(plan.TextureBytes)
        };
    }

    /** list_project_files: files under the project root, paged. */
    private static JsonObject AiProjectFiles(string? root, JsonObject args)
    {
        if (root is null || !Directory.Exists(root)) return AiError("No project is open.");
        string? filter = McpJson.String(args, "filter"), extension = McpJson.String(args, "extension");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(file => !file.Relative.StartsWith(".git/", StringComparison.Ordinal) && !file.Relative.StartsWith("build/", StringComparison.Ordinal))
            .Where(file => extension is not { Length: > 0 } || file.Relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Where(file => Matches(filter, file.Relative))
            .OrderBy(file => file.Relative, StringComparer.OrdinalIgnoreCase);
        return McpPages.Page(files, Offset(args), Limit(args), file => new JsonObject
        {
            ["path"] = file.Relative, ["bytes"] = new FileInfo(file.Path).Length
        });
    }

    /** list_project_changes: the project deltas and edits not yet built. */
    private JsonObject AiProjectChanges(JsonObject args)
    {
        if (workspace.Project is not { } project) return AiError("No project is open.");
        string kind = (McpJson.String(args, "kind") ?? "").ToLowerInvariant();
        var rows = new List<JsonObject>();
        if (kind is "" or "assets")
            rows.AddRange(project.Changes.Select(change => new JsonObject
            {
                ["kind"] = "asset", ["tag"] = Hex(change.TagHash), ["name"] = workspace.DisplayName(change.TagHash), ["package"] = change.PackageName,
                ["bytes"] = change.EditedSize
            }));
        if (kind is "" or "objects")
            rows.AddRange(project.ObjectEdits.Select(edit => new JsonObject
            {
                ["kind"] = "object", ["key"] = edit.Key, ["name"] = edit.Name, ["change"] = edit.Summary()
            }));
        if (kind is "" or "added")
            rows.AddRange(project.AddedAssets.Select(added => new JsonObject
            {
                ["kind"] = "added", ["id"] = added.Id, ["name"] = added.Name, ["bytes"] = added.Size,
                ["base"] = added.BaseTagHash is { } baseTag ? Hex(baseTag) : null
            }));
        string? filter = McpJson.String(args, "filter");
        return McpPages.Page(rows.Where(row => Matches(filter, row.ToJsonString())), Offset(args), Limit(args), row => row);
    }

    /** Maps and missions with zone names; rows are built once per world catalog. */
    private async Task<JsonObject> AiWorldListAsync(string tool, JsonObject args, CancellationToken cancel)
    {
        var rows = await OnUi(() =>
        {
            if (workspace.WorldCatalog is not { } catalog) return null;
            if (aiWorldRows is { } cached && ReferenceEquals(cached.Catalog, catalog)) return aiWorldRows;
            string Zone(uint hash) => workspace.ResolveHash(hash) ?? Hex(hash);
            JsonArray Zones(IEnumerable<uint> hashes) => new(hashes.Distinct().Select(hash => (JsonNode)Zone(hash)).ToArray());
            var maps = catalog.Maps.Select(map => new JsonObject
            {
                ["tag"] = Hex(map.Tag), ["name"] = map.Name, ["zones"] = Zones(map.BubbleHashes),
                ["missions"] = catalog.Missions.Count(mission => mission.MapTags.Contains(map.Tag))
            }).ToArray();
            var missions = catalog.Missions.Select(mission => new JsonObject
            {
                ["tag"] = Hex(mission.Tag), ["name"] = mission.Name, ["zones"] = Zones(mission.BubbleHashes),
                ["maps"] = new JsonArray(mission.MapTags.Select(tag => (JsonNode)Hex(tag)).ToArray())
            }).ToArray();
            aiWorldRows = (catalog, maps, missions);
            return aiWorldRows;
        }).WaitAsync(cancel).ConfigureAwait(false);
        if (rows is not { } lists)
            return AiError(workspace.Catalog is null ? "No installation is open." : "Solforge is still reading maps and missions; try again shortly.");
        string? filter = McpJson.String(args, "filter"), map = McpJson.String(args, "map");
        uint? mapTag = SolforgeTools.ParseHash(map);
        // list_missions is scoped to the open map unless a map is named or all=true.
        if (tool == "list_missions" && mapTag is null && !McpJson.Bool(args, "all", false))
            mapTag = await OnUi(() => workspace.World?.Map.Tag).WaitAsync(cancel).ConfigureAwait(false);
        var source = tool == "list_maps" ? lists.Maps : lists.Missions;
        var page = await Task.Run(() => McpPages.Page(source
            .Where(row => tool == "list_maps" || mapTag is not { } wanted || row["maps"] is JsonArray maps && maps.Any(node => node?.ToString() == Hex(wanted)))
            .Where(row => AllWords(filter, row.ToJsonString())), Offset(args), Limit(args), row => row.DeepClone()), cancel).ConfigureAwait(false);
        if (tool == "list_missions") page["map"] = mapTag is { } scoped ? Hex(scoped) : "all";
        return page;
    }

    /** list_packages: the installed packages. */
    private static JsonObject AiPackages(PackageCatalog? catalog, JsonObject args)
    {
        if (catalog is null) return AiError("No installation is open.");
        string? filter = McpJson.String(args, "filter");
        var packages = catalog.Entries.GroupBy(entry => entry.PackageName, StringComparer.OrdinalIgnoreCase)
            .Where(group => Matches(filter, group.Key)).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
        return McpPages.Page(packages, Offset(args), Limit(args), group => new JsonObject { ["name"] = group.Key, ["entries"] = group.Count() });
    }

    /** list_package_entries: entries of one package, paged. */
    private static JsonObject AiPackageEntries(PackageCatalog? catalog, StudioNameIndex? names, JsonObject args)
    {
        if (catalog is null) return AiError("No installation is open.");
        string package = McpJson.String(args, "package") ?? "", filter = McpJson.String(args, "filter") ?? "";
        var rows = catalog.Entries.Where(entry => string.Equals(entry.PackageName, package, StringComparison.OrdinalIgnoreCase))
            .Select(entry => (Entry: entry, Name: names?.Describe(entry) ?? catalog.GetName(entry.TagHash) ?? ""))
            .Where(row => Matches(filter, row.Name, Hex(row.Entry.TagHash)));
        var page = McpPages.Page(rows, Offset(args), Limit(args), row => new JsonObject
        {
            ["tag"] = Hex(row.Entry.TagHash), ["index"] = row.Entry.EntryIndex, ["type"] = row.Entry.Type, ["subtype"] = row.Entry.SubType,
            ["class"] = Hex(row.Entry.Reference), ["name"] = row.Name
        });
        if ((int)page["total"]! == 0 && !catalog.Entries.Any(entry => string.Equals(entry.PackageName, package, StringComparison.OrdinalIgnoreCase)))
            return AiError("No package by that name; use list_packages.");
        return page;
    }

    /** search_assets: searches the browser asset list. */
    private static JsonObject AiSearchAssets(BrowserAsset[] assets, JsonObject args)
    {
        string query = McpJson.String(args, "query") ?? "", kind = McpJson.String(args, "kind") ?? "";
        if (assets.Length == 0) return AiError("The asset index is not ready (no installation open, or names are still loading).");
        var rows = assets.Where(asset => (kind.Length == 0 || asset.Kind.Contains(kind, StringComparison.OrdinalIgnoreCase)) && AllWords(query, asset.SearchText));
        return McpPages.Page(rows, Offset(args), Limit(args), asset => new JsonObject
        {
            ["tag"] = Hex(asset.Entry.TagHash), ["name"] = asset.Name, ["kind"] = asset.Kind, ["package"] = asset.Entry.PackageName
        });
    }

    /** list_mission_scripts: the Lua scripts of the project and the game. */
    private static JsonObject AiMissionScripts(string? project, string? scripts, JsonObject args)
    {
        string? filter = McpJson.String(args, "filter");
        var roots = new[] { ("project", project), ("server", scripts) }.Where(root => root.Item2 is { Length: > 0 } path && Directory.Exists(path));
        var files = roots.SelectMany(root => Directory.EnumerateFiles(root.Item2!, "*.lua", SearchOption.AllDirectories)
                .Select(path => (Root: root.Item1, Path: path, Relative: Path.GetRelativePath(root.Item2!, path).Replace('\\', '/'))))
            .Where(file => Matches(filter, file.Relative)).OrderBy(file => file.Relative, StringComparer.OrdinalIgnoreCase);
        var page = McpPages.Page(files, Offset(args), Limit(args), file => new JsonObject
        {
            ["root"] = file.Root, ["path"] = file.Root == "project" ? file.Relative : ProjectFiles.ScriptsPrefix + file.Relative,
            ["fullPath"] = file.Root == "project" ? null : file.Path
        });
        if (scripts is not { Length: > 0 }) page["note"] = "No server mission scripts folder is set; set it with set_settings serverScripts (Settings > Paths).";
        else if (!Directory.Exists(scripts)) page["note"] = $"The server mission scripts folder {scripts} is missing; fix it with set_settings serverScripts.";
        return page;
    }

    /** Layer name (static, entity, and so on) of a world object source. */
    private static string AiLayer(object source) => source switch
    {
        SceneMarker marker => marker.Layer.ToString(),
        MeshInstance mesh => mesh.Layer.ToString(),
        _ => ""
    };

    /** Synonyms accepted for each layer name in find_world_objects. */
    private static readonly Dictionary<string, string[]> AiLayerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["volumes"] = ["Volumes", "Transitions"], ["collision"] = ["Collision"], ["meshes"] = ["Geometry", "Terrain", "Distant", "Skybox"],
        ["markers"] = ["Objects", "Spawns"], ["lights"] = ["Lights"], ["squads"] = ["Squads"], ["effects"] = ["Effects"]
    };

    /** find_world_objects: objects of the open world matching a query, layer or area. */
    private static JsonObject AiFindWorldObjects(WorldDocument? world, AiCapture capture, JsonObject args)
    {
        if (world is null) return AiError("No map is open; find it with list_maps and open it with open_map.");
        string? filter = McpJson.String(args, "filter"), layer = McpJson.String(args, "layer"), zone = McpJson.String(args, "zone");
        Vector3? near = args["near"] is JsonArray { Count: 3 } point ? new((float)point[0]!, (float)point[1]!, (float)point[2]!) : null;
        float radius = args["radius"] is JsonValue value && value.TryGetValue(out double distance) ? (float)distance : 100;
        string[]? layers = layer is { Length: > 0 } ? AiLayerNames.GetValueOrDefault(layer) ?? [layer] : null;
        var rows = world.Objects.Where(row =>
        {
            if (layers is not null && (layer!.Equals("authored", StringComparison.OrdinalIgnoreCase)
                    ? row.Kind != WorldObjectKind.AuthoredObject : !layers.Contains(AiLayer(row.Source), StringComparer.OrdinalIgnoreCase)))
                return false;
            if (zone is { Length: > 0 } && !row.BubbleName.Contains(zone, StringComparison.OrdinalIgnoreCase)) return false;
            if (near is { } center && (row.Position is not { } at || Vector3.Distance(at, center) > radius)) return false;
            return AllWords(filter, $"{AiSafeLabel(capture, row)} {row.Name} {row.Kind} {AiLayer(row.Source)} {row.BubbleName} {row.AssetTag:X8}");
        });
        if (near is { } origin) rows = rows.OrderBy(row => Vector3.Distance(row.Position!.Value, origin));
        /** A vector as a JSON array rounded to three places, or null. */
        static JsonArray? Vector(Vector3? v) => v is { } x ? new JsonArray(Math.Round(x.X, 3), Math.Round(x.Y, 3), Math.Round(x.Z, 3)) : null;
        var page = McpPages.Page(rows, Offset(args), Limit(args), row => new JsonObject
        {
            ["id"] = row.Id, ["name"] = AiSafeLabel(capture, row), ["kind"] = row.Kind.ToString(), ["layer"] = AiLayer(row.Source),
            ["zone"] = row.BubbleName, ["tag"] = row.AssetTag != 0 ? Hex(row.AssetTag) : null,
            ["position"] = Vector(row.Position), ["extents"] = Vector(row.Extents)
        });
        page["map"] = world.Map.Name;
        return page;
    }

    /** get_placement: details of one placed object. */
    private JsonObject AiPlacement(string id)
    {
        if (workspace.Catalog is not { } catalog) return AiError("No installation is open.");
        if (AiObject(id) is not { } row) return AiError("No object with that id in the open map.");
        var info = SceneEdits.Describe(catalog, row.Source, inspectionWorld);
        /** A vector as a JSON array rounded to three places. */
        static JsonArray Vector(Vector3 v) => new(Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3));
        var result = new JsonObject
        {
            ["id"] = row.Id, ["name"] = AiLabel(row.Source), ["layer"] = AiLayer(row.Source), ["zone"] = row.BubbleName,
            ["canMove"] = info.CanTransform, ["canScale"] = info.CanScale, ["canBounds"] = info.CanBounds,
            ["space"] = info.CoordinateSpace, ["reason"] = info.Reason
        };
        if (info.CanTransform)
        {
            result["position"] = Vector(info.Position);
            result["rotation"] = Vector(QuaternionToDegrees(info.Rotation));
            result["scale"] = Math.Round(info.Scale, 4);
        }
        if (info.CanBounds) { result["minimum"] = Vector(info.Minimum); result["maximum"] = Vector(info.Maximum); }
        if (info.Target is { } target && workspace.Project?.FindObjectEdit(target.Key) is { } saved)
            result["savedEdit"] = new JsonObject { ["key"] = saved.Key, ["change"] = saved.Summary() };
        if (!info.CanTransform && !info.CanBounds) result["note"] = "Read only: " + info.Reason;
        return result;
    }

    // Investment: the catalog loads in the background on first use; reads use the catalog snapshot off the UI thread.
    private async Task<JsonObject> AiInvestmentAsync(string tool, JsonObject args, CancellationToken cancel)
    {
        var (data, catalog, note) = await OnUi(() =>
        {
            if (workspace.Investment is { } loaded) return (loaded, workspace.Catalog, (string?)null);
            if (workspace.Catalog is null) return (null, null, "No installation is open.");
            if (!workspace.IsBusy) workspace.Run(workspace.LoadInvestment);
            return ((InvestmentCatalog?)null, (PackageCatalog?)null,
                "Solforge is reading the item catalog (about a minute the first time); call again shortly.");
        }).WaitAsync(cancel).ConfigureAwait(false);
        if (data is null || catalog is null) return AiError(note ?? "The item catalog is not loaded.");
        return await Task.Run(() => tool switch
        {
            "search_items" => AiSearchItems(data, args),
            "get_item" => AiItem(data, catalog, McpJson.String(args, "item") ?? ""),
            "search_perks" => McpPages.Page(data.PerkDefinitions.Where(perk => AllWords(McpJson.String(args, "query"), $"{perk.Index} {perk.Name} {perk.Description}")),
                Offset(args), Limit(args), perk => new JsonObject { ["index"] = perk.Index, ["name"] = perk.Name, ["description"] = perk.Description, ["hash"] = perk.Hash }),
            "list_vendors" => McpPages.Page(data.Vendors.Where(vendor => AllWords(McpJson.String(args, "filter"), $"{vendor.Name} {vendor.Tag:X8} {vendor.Hash}")),
                Offset(args), Limit(args), vendor => new JsonObject { ["tag"] = Hex(vendor.Tag), ["name"] = vendor.Name, ["hash"] = vendor.Hash, ["index"] = vendor.Index }),
            _ => AiVendor(data, catalog, args)
        }, cancel).WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
    }

    /** Finds an item by hash or name. */
    private static InvestmentItem? AiFindItem(InvestmentCatalog data, string text)
    {
        text = text.Trim();
        if (SolforgeTools.ParseHash(text) is { } hash && (data.FindItem(hash) ?? data.Items.FirstOrDefault(item => item.Hash == hash)) is { } byHash) return byHash;
        if (int.TryParse(text, out int index) && index >= 0 && index < data.Items.Count) return data.Items[index];
        return data.Items.FirstOrDefault(item => string.Equals(item.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    /** Finds a vendor by hash or name. */
    private static InvestmentVendor? AiFindVendor(InvestmentCatalog data, string text)
    {
        text = text.Trim();
        if (SolforgeTools.ParseHash(text) is { } hash && (data.FindVendor(hash) ?? data.Vendors.FirstOrDefault(vendor => vendor.Hash == hash)) is { } byHash) return byHash;
        return data.Vendors.FirstOrDefault(vendor => string.Equals(vendor.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    /** search_items: items matching a text query. */
    private static JsonObject AiSearchItems(InvestmentCatalog data, JsonObject args)
    {
        string? query = McpJson.String(args, "query"), category = McpJson.String(args, "category");
        var rows = data.Items.Where(item => (category is not { Length: > 0 } || item.Category.ToString().Equals(category, StringComparison.OrdinalIgnoreCase))
            && AllWords(query, $"{item.Name} {item.TypeName} {item.Rarity} {item.Category} {item.Tag:X8} {item.Hash}"));
        return McpPages.Page(rows, Offset(args), Limit(args), item => new JsonObject
        {
            ["tag"] = Hex(item.Tag), ["name"] = item.Name, ["type"] = item.TypeName, ["rarity"] = item.Rarity,
            ["category"] = item.Category.ToString(), ["hash"] = item.Hash, ["index"] = item.Index
        });
    }

    /** get_item: one item with stats, perks and sockets. */
    private static JsonObject AiItem(InvestmentCatalog data, PackageCatalog catalog, string text)
    {
        if (AiFindItem(data, text) is not { } item) return AiError("No item by that tag, hash or name; use search_items.");
        var detail = data.ReadItem(item.Tag, catalog.Read(item.Tag));
        return new JsonObject
        {
            ["tag"] = Hex(item.Tag), ["displayTag"] = Hex(item.DisplayTag), ["hash"] = item.Hash, ["index"] = item.Index, ["name"] = item.Name,
            ["type"] = item.TypeName, ["description"] = item.Description, ["rarity"] = item.Rarity, ["bucket"] = item.Bucket,
            ["category"] = item.Category.ToString(),
            ["stats"] = new JsonArray(detail.Stats.Select(stat => (JsonNode)new JsonObject
            { ["index"] = stat.Index, ["name"] = stat.Name, ["value"] = stat.Value, ["computed"] = stat.Computed }).ToArray()),
            ["sockets"] = new JsonArray(detail.Sockets.Select(socket => (JsonNode)new JsonObject
            { ["index"] = socket.Index, ["type"] = socket.Type, ["plug"] = socket.PlugName, ["plugIndex"] = socket.InitialPlug, ["choices"] = socket.AllowedPlugs.Count }).ToArray()),
            ["perks"] = new JsonArray(detail.Perks.Select(perk => (JsonNode)new JsonObject { ["index"] = perk.Index, ["name"] = perk.Name }).ToArray()),
            ["fields"] = new JsonArray(detail.Fields.Where(field => field.Editable).Select(field => (JsonNode)new JsonObject
            { ["name"] = field.Name, ["value"] = field.Value }).ToArray()),
            ["diagnostics"] = new JsonArray(detail.Diagnostics.Select(line => (JsonNode)line).ToArray())
        };
    }

    /** get_vendor: one vendor with sale items. */
    private static JsonObject AiVendor(InvestmentCatalog data, PackageCatalog catalog, JsonObject args)
    {
        if (AiFindVendor(data, McpJson.String(args, "vendor") ?? "") is not { } vendor) return AiError("No vendor by that tag, hash or name; use list_vendors.");
        var detail = data.ReadVendor(vendor.Tag, catalog.Read(vendor.Tag));
        var page = McpPages.Page(detail.Sales, Offset(args), Limit(args), sale => new JsonObject
        {
            ["sale"] = sale.Index, ["item"] = sale.Name, ["itemIndex"] = sale.ItemIndex, ["category"] = sale.Category,
            ["costs"] = new JsonArray(sale.Costs.Select((cost, index) => (JsonNode)new JsonObject
            { ["cost"] = index, ["item"] = cost.Name, ["itemIndex"] = cost.ItemIndex, ["quantity"] = cost.Quantity }).ToArray())
        });
        page["vendor"] = vendor.Name; page["tag"] = Hex(vendor.Tag);
        return page;
    }

    /** list_creation_types: the things the Create flow can make. */
    private static JsonObject AiCreationTypes(string? filter)
    {
        var rows = CreationTypes.All.Concat(CreationTypes.Blocked.Select(type => (Key: type.Name, Mode: CreationMode.New, Type: type)))
            .Where(row => AllWords(filter, $"{row.Key} {row.Mode} {row.Type.Name} {row.Type.Summary} {row.Type.Blocker}"));
        return McpPages.Page(rows, 0, McpPages.MaximumLimit, row => new JsonObject
        {
            ["key"] = row.Key, ["mode"] = row.Mode.ToString().ToLowerInvariant(), ["name"] = row.Type.Name, ["summary"] = row.Type.Summary,
            ["available"] = row.Type.Available && row.Type.Factory is not null, ["blocker"] = row.Type.Blocker.Length > 0 ? row.Type.Blocker : null
        });
    }

    /** Parses a creation mode name (new, clone, ...); null when unknown. */
    private static CreationMode? AiMode(string mode) => mode.ToLowerInvariant() switch { "new" => CreationMode.New, "clone" => CreationMode.Clone, _ => null };

    /** describe_creation: fields and steps of a creation type. */
    private JsonObject AiDescribeCreation(string key, string mode)
    {
        if (AiMode(mode) is not { } parsed) return AiError("mode must be new or clone.");
        var type = CreationTypes.For(key, parsed);
        if (!type.Available) return AiError($"{type.Name}: {type.Blocker}");
        if (type.Factory is not { } factory) return new JsonObject { ["name"] = type.Name, ["note"] = "Duplicate copies the source asset; it has no fields." };
        var workflow = factory(workspace);
        return new JsonObject
        {
            ["title"] = workflow.Title, ["create"] = workflow.CreateLabel,
            ["limits"] = new JsonArray(workflow.Limits.Select(limit => (JsonNode)limit).ToArray()),
            ["cloneSource"] = workflow.CloneSource?.Label,
            ["fields"] = new JsonArray(workflow.Inputs.Select(field => (JsonNode)new JsonObject
            {
                ["label"] = field.Label, ["kind"] = field.Kind.ToString(), ["required"] = field.Required,
                ["hint"] = field.Hint.Length > 0 ? field.Hint : null, ["value"] = field.Kind == CreationFieldKind.Tag
                    ? field.Tag is { } tag ? Hex(tag) : null : field.Text.Length > 0 ? field.Text : null
            }).ToArray())
        };
    }

    /**
     * Engine start: copies the bundled playbooks into ~/.claude/skills when they differ, then writes .solforge/SESSION.md and
     * the AGENTS.md block in the project. Failures only cost the engine its context, so they are noted, not thrown.
     */
    private void AiWriteSession(string folder)
    {
        try
        {
            SkillSync.Sync(Path.Combine(AppContext.BaseDirectory, "skills"), SkillSync.Folder);
            var tools = aiHost is null ? [] : SolforgeTools.Create(aiHost).Select(tool => tool.Name).ToArray();
            var facts = new SessionFacts(workspace.Project?.Name ?? Path.GetFileName(folder), workspace.Catalog?.GameRoot,
                workspace.World?.Map.Name, workspace.World?.MissionTag is { } mission ? Hex(mission) : null,
                workspace.Document is { } document ? $"{document.Name} ({Hex(document.Entry.TagHash)})" : null,
                missionGraph.ScriptPath is { } script ? AiShownPath(script) : null, settings.AiMcpEnabled && aiService is not null,
                SkillSync.Names(SkillSync.Folder), tools);
            SessionContext.Write(folder, facts);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            aiInstallNote = ("Could not write the session context: " + error.Message, false);
        }
    }

    // Actions, on the UI thread after the approve click.
    private string? RunAiProjectAction(string tool, JsonObject args)
    {
        switch (tool)
        {
            case "open_map":
            {
                // Missions belong to maps: a mission alone opens on its own map, and a map alone lists its missions.
                uint? mission = SolforgeTools.ParseHash(McpJson.String(args, "mission"));
                var entry = mission is { } wanted ? workspace.WorldCatalog?.Missions.FirstOrDefault(row => row.Tag == wanted) : null;
                uint? map = SolforgeTools.ParseHash(McpJson.String(args, "map"));
                if (map is null && mission is not null)
                {
                    if (entry is null) return workspace.WorldCatalog is null ? "Solforge is still reading maps and missions; try again shortly."
                        : $"No mission {Hex(mission.Value)}; find it with list_missions.";
                    if (entry.MapTags.Count == 0) return $"Mission {entry.Name} names no map.";
                    map = workspace.World is { } open && entry.MapTags.Contains(open.Map.Tag) ? open.Map.Tag : entry.MapTags[0];
                }
                if (map is not { } target) return "Give a map tag (list_maps) or a mission tag (list_missions).";
                if (entry is not null && entry.MapTags.Count > 0 && !entry.MapTags.Contains(target))
                    return $"Mission {entry.Name} loads on {string.Join(", ", entry.MapTags.Select(Hex))}, not {Hex(target)}; leave map out to use its own.";
                if (workspace.IsBusy) return "Solforge is busy (" + workspace.Status + "); try again when get_editor_state says it is idle.";
                if (mission is { } chosen) workspace.Run(() => workspace.OpenMission(target, chosen));
                else workspace.Run(() => workspace.OpenMap(target));
                if (workspace.Error.Length > 0) return workspace.Error;
                var missions = workspace.WorldCatalog?.Missions.Where(row => row.MapTags.Contains(target)).ToArray() ?? [];
                string listed = missions.Length == 0 ? "" : $" Its {missions.Length} mission(s): " +
                    string.Join(", ", missions.Take(8).Select(row => $"{row.Name} {Hex(row.Tag)}")) + (missions.Length > 8 ? ", ..." : "") + ".";
                return (mission is { } opened ? $"Opening mission {entry?.Name ?? Hex(opened)} on map {Hex(target)}" : $"Opening map {Hex(target)}")
                    + "; poll get_editor_state until busy is false." + (mission is null ? listed + " open_map with mission opens one." : "");
            }
            case "open_asset":
            {
                if (SolforgeTools.ParseHash(McpJson.String(args, "tag")) is not { } tag) return "Give a tag.";
                workspace.Run(() => workspace.Select(tag));
                return workspace.Error.Length > 0 ? workspace.Error : $"Opened {workspace.DisplayName(tag)}.";
            }
            case "set_viewport_rendering":
            {
                if (args["on"] is JsonValue) SetViewportRendering(McpJson.Bool(args, "on", true));
                if (McpJson.String(args, "quality") is { Length: > 0 } name)
                {
                    if (!Enum.TryParse<SunriseStudio.Viewport.ViewportQuality>(name, true, out var quality) || !Enum.IsDefined(quality))
                        return "quality must be low, medium, high or full.";
                    SetViewportQuality(quality);
                }
                return (settings.ViewportRendering ? "3D viewport rendering is on" : "3D viewport rendering is off")
                    + $", quality {settings.ViewportQuality}" + (GameShaderProblem() is { } problem ? ". " + problem : ".");
            }
            case "edit_placement": return AiEditPlacement(args, bounds: false);
            case "edit_bounds": return AiEditPlacement(args, bounds: true);
            case "revert_object_edit":
            {
                string key = McpJson.String(args, "key") ?? "";
                if (workspace.Project?.FindObjectEdit(key) is null) return "No saved object edit with that key (list_project_changes kind=objects).";
                workspace.Run(() => workspace.RevertObjectEdit(key));
                editTarget = null;
                return workspace.Error.Length > 0 ? workspace.Error : "Reverted.";
            }
            case "apply_item_edit": return AiApplyItemEdit(args);
            case "apply_graph": return AiApplyGraph(args);
            case "create_mission_script": return AiCreateMissionScript(args);
            case "apply_vendor_edit": return AiApplyVendorEdit(args);
            case "revert_to_base": return AiRevertToBase(args);
            case "retail_open_map": return AiRetailOpenMap(args);
            case "start_creation": return AiStartCreation(args);
            case "start_parhelion_weapon_recipe": return AiStartParhelionWeaponRecipe(args);
            case "replace_texture_pixels":
            {
                if (SolforgeTools.ParseHash(McpJson.String(args, "tag")) is not { } tag) return "Give the texture tag.";
                if (!workspace.CanEdit) return "No project is open, or Solforge is busy.";
                if (workspace.Document?.Entry.TagHash != tag) { workspace.Run(() => workspace.Select(tag)); return "Opened the texture; call again once it shows."; }
                if (workspace.Document is not { Texture: { } texture } document) return "That asset is not a texture.";
                string path = McpJson.String(args, "path") ?? "";
                workspace.Run(() => workspace.ApplyAssetEdits(TextureEdits.Apply(workspace.Catalog!, document.Entry.TagHash,
                    path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? DdsInterchange.Read(path)
                        : PngInterchange.ToTexture(PngInterchange.Read(path), texture))));
                return workspace.Error.Length > 0 ? workspace.Error : $"Replaced the pixels of {document.Name}; it is in Project changes with undo.";
            }
            case "build_project":
                if (!workspace.CanEdit) return "No project is open, or Solforge is busy.";
                if (McpJson.Bool(args, "install", false))
                {
                    if (destiny2Detected) return "Destiny 2 is running; close the game before installing.";
                    // Checked here so the AI gets an answer instead of the Mod details dialog or a save prompt.
                    if (workspace.Project?.Mod is null) return "The project has no mod details; set them with set_mod_details first.";
                    if (missionGraph.IsDirty) return AiDraftOpen;
                    InstallMod();
                }
                else workspace.Run(() => workspace.Build());
                return workspace.Error.Length > 0 ? workspace.Error : "Started; poll get_build_status.";
            default:
                return RunAiSelfServiceAction(tool, args);
        }
    }

    /** A JSON array as a Vector3, or null. */
    private static Vector3? AiVector(JsonNode? node) =>
        node is JsonArray { Count: 3 } array ? new((float)array[0]!, (float)array[1]!, (float)array[2]!) : null;

    /** Edits a placement position/rotation (or bounds) for apply_project_edit; returns result text. */
    private string AiEditPlacement(JsonObject args, bool bounds)
    {
        if (workspace.Catalog is not { } catalog) return "No installation is open.";
        if (!workspace.CanEdit) return "No project is open, or Solforge is busy.";
        if (AiObject(McpJson.String(args, "id") ?? "") is not { } row) return "No object with that id in the open map.";
        var info = SceneEdits.Describe(catalog, row.Source, inspectionWorld);
        if (bounds)
        {
            if (!info.CanBounds) return "This object has no editable bounds: " + info.Reason;
            if (AiVector(args["minimum"]) is not { } minimum || AiVector(args["maximum"]) is not { } maximum) return "Give minimum and maximum as [x, y, z].";
            workspace.Run(() => workspace.ApplyObjectEdit(SceneEdits.CreateBoundsEdit(catalog, row.Source, minimum, maximum, inspectionWorld, row.Name)));
        }
        else
        {
            if (!info.CanTransform) return "This object's placement is read only: " + info.Reason;
            var position = AiVector(args["position"]) ?? info.Position;
            Quaternion? rotation = AiVector(args["rotation"]) is { } degrees ? DegreesToQuaternion(degrees) : null;
            float scale = args["scale"] is JsonValue value && value.TryGetValue(out double size) ? (float)size : info.Scale;
            if (!info.CanScale && Math.Abs(scale - info.Scale) > 1e-5f) return "This object cannot be scaled.";
            workspace.Run(() => workspace.ApplyObjectEdit(SceneEdits.CreateTransformEdit(catalog, row.Source, position, rotation, scale, inspectionWorld, row.Name)));
        }
        editTarget = null;
        if (workspace.Error.Length > 0) return workspace.Error;
        if (workspace.ObjectEditError.Length > 0) return workspace.ObjectEditError;
        var saved = info.Target is { } target ? workspace.Project?.FindObjectEdit(target.Key) : null;
        return saved is null ? "Nothing changed." : $"Saved object edit {saved.Key}: {saved.Summary()}. Build to test it in game.";
    }

    /** The item or vendor detail must be the open document; otherwise open it so the next call can apply. */
    private string? AiInvestmentReady(uint tag, bool vendor)
    {
        if (workspace.Investment is null) { if (!workspace.IsBusy) workspace.Run(workspace.LoadInvestment); return "The item catalog is loading; call again shortly."; }
        if (workspace.Document?.Entry.TagHash != tag) { workspace.Run(() => workspace.Select(tag)); return "Opened it in Solforge; call again once it is read."; }
        bool current = workspace.InvestmentDetailCurrent && (vendor ? workspace.SelectedInvestmentVendor is not null : workspace.SelectedInvestmentItem is not null);
        if (!current)
        {
            if (!workspace.IsBusy && !workspace.InvestmentDetailCurrent) workspace.Run(workspace.LoadInvestmentDetail);
            return "Solforge is reading it; call again shortly.";
        }
        return workspace.CanEdit ? null : "No project is open, or Solforge is busy.";
    }

    /** Applies an item edit op list (apply_item_edit); returns result text. */
    private string AiApplyItemEdit(JsonObject args)
    {
        var data = workspace.Investment;
        if (data is not null && AiFindItem(data, McpJson.String(args, "item") ?? "") is null) return "No such item; use search_items.";
        uint tag = data is not null ? AiFindItem(data, McpJson.String(args, "item") ?? "")!.Tag : SolforgeTools.ParseHash(McpJson.String(args, "item")) ?? 0;
        if (AiInvestmentReady(tag, vendor: false) is { } wait) return wait;
        var detail = workspace.SelectedInvestmentItem!;
        string op = McpJson.String(args, "op") ?? "", value = McpJson.String(args, "value") ?? "";
        string statText = McpJson.String(args, "stat") ?? "", perkText = McpJson.String(args, "perk") ?? "";
        switch (op)
        {
            case "set_field":
                string name = McpJson.String(args, "field") ?? "";
                if (detail.Fields.FirstOrDefault(field => field.Editable && string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } target)
                    return "No editable field by that name; get_item lists them.";
                workspace.Run(() => workspace.ApplyInvestmentEdit(target, value));
                break;
            case "add_stat":
                byte? stat = byte.TryParse(statText, out byte index) ? index
                    : data!.StatDefinitions.FirstOrDefault(row => string.Equals(row.Name, statText, StringComparison.OrdinalIgnoreCase))?.Index;
                if (stat is not { } chosen) return "No stat by that index or name.";
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount)) return "Give the stat value as a whole number.";
                workspace.Run(() => workspace.AddInvestmentStat(chosen, amount));
                break;
            case "remove_stat":
                if (detail.Stats.FirstOrDefault(row => row.Index.ToString(CultureInfo.InvariantCulture) == statText
                    || string.Equals(row.Name, statText, StringComparison.OrdinalIgnoreCase)) is not { } removed) return "The item has no such stat.";
                workspace.Run(() => workspace.RemoveInvestmentStat(removed));
                break;
            case "add_perk":
                int? perk = ushort.TryParse(perkText, out ushort perkIndex) ? perkIndex
                    : data!.PerkDefinitions.FirstOrDefault(row => string.Equals(row.Name, perkText, StringComparison.OrdinalIgnoreCase))?.Index;
                if (perk is not { } added || added > ushort.MaxValue) return "No perk by that index or name; use search_perks.";
                workspace.Run(() => workspace.AddInvestmentPerk((ushort)added));
                break;
            case "remove_perk":
                if (detail.Perks.FirstOrDefault(row => row.Index.ToString(CultureInfo.InvariantCulture) == perkText
                    || string.Equals(row.Name, perkText, StringComparison.OrdinalIgnoreCase)) is not { } gone) return "The item has no such perk.";
                workspace.Run(() => workspace.RemoveInvestmentPerk(gone));
                break;
            default:
                return "Unknown op.";
        }
        return workspace.Error.Length > 0 ? workspace.Error : $"Applied {op} to {detail.Item.Name}; it is in Project changes with undo.";
    }

    /** Applies a vendor edit (apply_vendor_edit); returns result text. */
    private string AiApplyVendorEdit(JsonObject args)
    {
        var data = workspace.Investment;
        uint tag = data is not null && AiFindVendor(data, McpJson.String(args, "vendor") ?? "") is { } found ? found.Tag
            : SolforgeTools.ParseHash(McpJson.String(args, "vendor")) ?? 0;
        if (AiInvestmentReady(tag, vendor: true) is { } wait) return wait;
        var detail = workspace.SelectedInvestmentVendor!;
        int saleIndex = McpJson.Int(args, "sale", -1);
        if (detail.Sales.FirstOrDefault(sale => sale.Index == saleIndex) is not { } sale) return "No sale with that index; get_vendor lists them.";
        string op = McpJson.String(args, "op") ?? "";
        ushort? Item() => McpJson.String(args, "item") is { Length: > 0 } text
            ? ushort.TryParse(text, out ushort index) ? index : AiFindItem(data!, text) is { } item ? (ushort)item.Index : null : null;
        switch (op)
        {
            case "duplicate_sale": workspace.Run(() => workspace.DuplicateInvestmentSale(sale)); break;
            case "remove_sale": workspace.Run(() => workspace.RemoveInvestmentSale(sale)); break;
            case "set_sale_item":
                if (Item() is not { } item) return "Give the item by index, tag or exact name.";
                workspace.Run(() => workspace.ApplyInvestmentEdit(sale.ItemField, item.ToString(CultureInfo.InvariantCulture)));
                break;
            case "edit_cost":
                int costIndex = McpJson.Int(args, "cost", -1);
                if (costIndex < 0 || costIndex >= sale.Costs.Count) return "No cost row with that index.";
                var cost = sale.Costs[costIndex];
                ushort costItem = Item() ?? cost.ItemIndex;
                uint quantity = (uint)Math.Max(0, McpJson.Int(args, "quantity", (int)cost.Quantity));
                workspace.Run(() => workspace.EditInvestmentCost(sale, cost, costItem, quantity));
                break;
            default:
                return "Unknown op.";
        }
        return workspace.Error.Length > 0 ? workspace.Error : $"Applied {op} to {detail.Vendor.Name}; it is in Project changes with undo.";
    }

    /** Starts a creation flow (new/clone item etc.) from tool arguments; returns result text. */
    private string AiStartCreation(JsonObject args)
    {
        if (AiMode(McpJson.String(args, "mode") ?? "") is not { } mode) return "mode must be new or clone.";
        var type = CreationTypes.For(McpJson.String(args, "key") ?? "", mode);
        if (!type.Available) return $"{type.Name}: {type.Blocker}";
        if (!workspace.CanEdit) return "No project is open, or Solforge is busy.";
        uint? source = SolforgeTools.ParseHash(McpJson.String(args, "source"));
        if (type.Factory is null) { StartCreation(type, source, source is { } tag ? workspace.DisplayName(tag) : ""); return "Started the Duplicate."; }
        if (source is { } picked && workspace.CloneBaseOf(picked) is var owner && owner != picked) source = owner;
        var workflow = type.Factory(workspace);
        if (source is { } from && workflow.CloneSource is { } sourceField) { sourceField.Tag = from; sourceField.TagLabel = workspace.DisplayName(from); }
        var missing = new List<string>();
        foreach (var (label, node) in args["fields"] as JsonObject ?? new JsonObject())
        {
            var field = workflow.Inputs.FirstOrDefault(input => string.Equals(input.Label, label, StringComparison.OrdinalIgnoreCase));
            string text = node?.ToString() ?? "";
            if (field is null) { missing.Add(label); continue; }
            if (field.Kind == CreationFieldKind.Tag && SolforgeTools.ParseHash(text) is { } tag) { field.Tag = tag; field.TagLabel = workspace.DisplayName(tag); }
            else if (field.Kind is CreationFieldKind.Text or CreationFieldKind.Scalar or CreationFieldKind.File) field.Text = text;
            else missing.Add(label);
        }
        creation.Open(workflow);
        return $"Opened {workflow.Title} in Solforge; the user reviews it and presses {workflow.CreateLabel}."
            + (missing.Count > 0 ? " Not filled (unknown label or kind): " + string.Join(", ", missing) + "." : "");
    }

    /** Opens the normal clone dialog from an approved, project-local recipe whose exact bytes were reviewed. */
    private string AiStartParhelionWeaponRecipe(JsonObject args)
    {
        if (!workspace.CanEdit) return "Open a project and installation first.";
        string relative = McpJson.String(args, "path") ?? "";
        if (!relative.EndsWith(WeaponRecipeJson.Extension, StringComparison.OrdinalIgnoreCase))
            return "Give a project-local *.weapon.json path.";
        if (ProjectFiles.Resolve(workspace.Project?.DirectoryPath, aiHost?.SdkRoots ?? [], relative) is not { } path
            || !File.Exists(path)) return "Recipe path is missing or outside the active project.";
        var info = new FileInfo(path);
        if (info.Length > 1 << 20) return "Recipe exceeds the 1 MiB limit.";
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), McpJson.String(args, "expected_sha256"), StringComparison.OrdinalIgnoreCase))
            return "The recipe changed after approval. Validate and start it again.";
        WeaponRecipe recipe;
        try { recipe = WeaponRecipeJson.Parse(new System.Text.UTF8Encoding(false, true).GetString(bytes)); }
        catch (Exception error) when (error is WeaponRecipeException or System.Text.DecoderFallbackException)
        { return error.Message; }
        var workflow = new ItemWorkflow(workspace, recipe);
        creation.Open(workflow);
        return $"Opened '{recipe.Name}' in Solforge's weapon clone workflow. Review the listed fields and notes, then press Create to apply the supported recipe fields.";
    }
}
