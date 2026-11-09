using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Solforge.Content;
using Solforge.Mcp;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using McpPages = Solforge.Mcp.Pages;

namespace SunriseStudio.Editor;

/**
 * T1.5 compare_with_base / revert_to_base and T1.4 get_item_sources (SolforgeTools.ItemSources.cs defines the tools).
 * The item report is built once here and shared by the MCP tool and the "Where is this used" panel.
 */
public sealed partial class StudioForm
{
    /** Note returned until a loot-table decoder exists (tag-schemas branch: get_loot_table). */
    private const string LootTablesPending = "loot tables: decoder pending";
    /** Wall-clock budget for each bounded scan (vendors, plugs). */
    private static readonly TimeSpan ItemScanBudget = TimeSpan.FromSeconds(20);

    /** Dispatches compare_with_base and get_item_sources. */
    private async Task<JsonObject> AiItemSourceQueryAsync(string tool, JsonObject args, CancellationToken cancel) => tool switch
    {
        "compare_with_base" => await AiCompareWithBaseAsync(args, cancel).ConfigureAwait(false),
        "check_item_perk_displays" => await OnUi(AiCheckPerkDisplays).WaitAsync(cancel).ConfigureAwait(false),
        _ => await AiItemSourcesAsync(args, cancel).ConfigureAwait(false)
    };

    // ---- check_item_perk_displays / repair_item_perk_displays ----

    /** Perk count vs display requirement count for every project item, plus display tags several items claim. */
    private JsonObject AiCheckPerkDisplays()
    {
        if (!workspace.CanEdit || workspace.Project is null) return AiError("No project is open, or Solforge is busy.");
        if (workspace.Investment is null) return AiError("The item catalog is not loaded yet; call get_item or search_items, then try again.");
        var (items, shared) = workspace.CheckPerkDisplays();
        var rows = new JsonArray();
        foreach (var row in items)
            rows.Add(new JsonObject
            {
                ["item"] = Hex(row.Item), ["name"] = row.Name, ["display"] = Hex(row.Display), ["perks"] = row.Perks,
                ["displayRows"] = row.DisplayRows, ["ok"] = !row.Mismatched, ["problem"] = row.Problem
            });
        var claims = new JsonArray();
        foreach (var (display, owners) in shared)
            claims.Add(new JsonObject { ["display"] = Hex(display), ["items"] = new JsonArray([.. owners.Select(tag => (JsonNode)Hex(tag))]) });
        return new JsonObject
        {
            ["checked"] = items.Count, ["mismatched"] = items.Count(row => row.Mismatched), ["items"] = rows, ["sharedDisplays"] = claims,
            ["note"] = "A mismatch crashes the game client (it walks perks and display rows with one index). repair_item_perk_displays fixes it."
        };
    }

    /** Resizes mismatched display rows to the perk counts as one undo step (repair_item_perk_displays). */
    private string AiRepairPerkDisplays(JsonObject args)
    {
        if (!workspace.CanEdit || workspace.Project is null) return "No project is open, or Solforge is busy.";
        if (workspace.Investment is null) return "The item catalog is not loaded yet; call get_item or search_items, then try again.";
        bool dryRun = McpJson.Bool(args, "dry_run", true);
        if (!dryRun && workspace.Document is null) return "Open any asset first (open_asset); the repair is recorded as an undo step against it.";
        (IReadOnlyList<PerkDisplayRow> Fixed, IReadOnlyList<string> Skipped) result = ([], []);
        workspace.Run(() => result = workspace.RepairPerkDisplays(dryRun));
        if (workspace.Error.Length > 0) return workspace.Error;
        string list = string.Join("; ", result.Fixed.Select(row => $"{row.Name} {Hex(row.Item)} (perks {row.Perks}, display rows {row.DisplayRows} -> {row.Perks})"));
        string skipped = result.Skipped.Count == 0 ? "" : " Skipped: " + string.Join("; ", result.Skipped) + ".";
        if (result.Fixed.Count == 0) return "No project item needs its display rows resized." + skipped;
        return (dryRun ? $"Dry run: would fix {result.Fixed.Count} item(s): " : $"Fixed {result.Fixed.Count} item(s) in one undo step: ") + list + "." + skipped
            + (dryRun ? " Call again with dry_run false to apply." : "");
    }

    // ---- T1.5 compare_with_base ----

    private async Task<JsonObject> AiCompareWithBaseAsync(JsonObject args, CancellationToken cancel)
    {
        if (SolforgeTools.ParseHash(McpJson.String(args, "hash")) is not { } tag) return AiError("Give a tag hash such as 0x80A1B2C3.");
        string? field = McpJson.String(args, "field");
        var (project, catalog) = await OnUi(() => (workspace.Project, workspace.Catalog)).WaitAsync(cancel).ConfigureAwait(false);
        if (project is null || catalog is null) return AiError("Open the installation and a project to compare with base.");
        return await Task.Run(() =>
        {
            if (!catalog.TryGetEntry(tag, out _)) return AiError($"No tag {Hex(tag)} in the installation.");
            var diff = ProjectTagDiff.Compare(project, tag, catalog);
            var fields = TagFields(tag, diff.Authored, catalog);
            if (field is { Length: > 0 } && TagBaseDiff.Find(fields, field) is null && TagBaseDiff.ParseRange(field) is null)
                return AiError($"No field '{field}' on {Hex(tag)}. Known fields: " +
                    (fields.Count == 0 ? "none (no decoder or schema; give an offset such as 0x40 or a range 0x40+8)" : string.Join(", ", fields.Take(40).Select(f => f.Name))));
            return CompareJson(TagBaseDiff.Compare(diff, fields, field), project.ContainsEdit(tag), workspace.ResolveName(tag));
        }, cancel).WaitAsync(AiReadTimeout, cancel).ConfigureAwait(false);
    }

    private static JsonObject CompareJson(TagBaseComparison comparison, bool edited, string? name)
    {
        var diff = comparison.Diff;
        return new JsonObject
        {
            ["tag"] = Hex(diff.TagHash), ["name"] = name, ["edited"] = edited, ["identical"] = diff.Identical,
            ["baseLength"] = diff.Base.Length, ["projectLength"] = diff.Authored.Length,
            ["changedBytes"] = diff.ChangedBytes, ["changedWords"] = diff.ChangedWords,
            ["layout"] = comparison.Known.Count == 0 ? "none: byte ranges only" : string.Join(", ", comparison.Known.Select(f => f.Source).Distinct()),
            ["fields"] = new JsonArray(comparison.Fields.Select(change => (JsonNode)new JsonObject
            {
                ["field"] = change.Field.Name, ["offset"] = $"0x{change.Field.Offset:X}", ["size"] = change.Field.Size,
                ["kind"] = change.Field.Kind, ["source"] = change.Field.Source, ["base"] = change.Base, ["project"] = change.Authored
            }).ToArray()),
            ["ranges"] = new JsonArray(comparison.Ranges.Select(range => (JsonNode)new JsonObject
            {
                ["offset"] = $"0x{range.Offset:X}", ["length"] = range.Length, ["base"] = range.Base, ["project"] = range.Authored
            }).ToArray()),
            ["rangeCount"] = comparison.RangeCount,
            ["next"] = edited ? "revert_to_base (hash, optional field) undoes the change" : "the project does not change this tag"
        };
    }

    /**
     * Named fields of a tag: the typed decoder's scalar properties (the inspector rows that carry an offset and
     * encoding). Schema fields join here once the tag-schemas registry lands.
     */
    private List<TagFieldSpan> TagFields(uint tag, byte[] data, PackageCatalog catalog)
    {
        var fields = new List<TagFieldSpan>();
        if (!catalog.TryGetEntry(tag, out var entry)) return fields;
        var context = new AssetInspectionContext(tag, entry.Reference, data, workspace.ResolveName(tag) ?? "",
            workspace.ResolveName, catalog.ContainsTag, catalog.Read);
        // Only the typed decoder: the binary fallback's per-word rows would name every word.
        if (workspace.Inspectors.Inspectors.FirstOrDefault(inspector => inspector.CanInspect(context)) is { } decoder)
        {
            try
            {
                void Walk(IEnumerable<AssetNode> nodes, string path)
                {
                    foreach (var node in nodes)
                    {
                        string here = path.Length == 0 ? node.Name : path + " / " + node.Name;
                        foreach (var property in node.Properties)
                            if (property.Offset >= 0 && property.Encoding != ScalarEncoding.None)
                                fields.Add(new(here + " / " + property.Name, property.Offset, AssetEdit.SizeOf(property.Encoding),
                                    Kind(property.Encoding), "decoder:" + decoder.DisplayName));
                        if (node.Children is { } children) Walk(children, here);
                    }
                }
                Walk(decoder.Inspect(context).Sections, "");
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or OverflowException or IndexOutOfRangeException) { }
        }
        // TODO(tag-schemas): add the schema registry's fields for entry.Reference here with Source "schema:<class>".
        return fields.DistinctBy(field => (field.Offset, field.Size)).ToList();
    }

    private static string Kind(ScalarEncoding encoding) => encoding switch
    {
        ScalarEncoding.UInt8 => "u8", ScalarEncoding.Int16 => "i16", ScalarEncoding.UInt16 => "u16", ScalarEncoding.Int32 => "i32",
        ScalarEncoding.UInt32 => "u32", ScalarEncoding.Int64 => "i64", ScalarEncoding.UInt64 => "u64", ScalarEncoding.Float32 => "f32", _ => "bytes"
    };

    // ---- T1.5 revert_to_base (UI thread, after the approve click) ----

    private string AiRevertToBase(JsonObject args)
    {
        if (SolforgeTools.ParseHash(McpJson.String(args, "hash")) is not { } tag) return "Give a tag hash such as 0x80A1B2C3.";
        if (!workspace.CanEdit || workspace.Project is not { } project || workspace.Catalog is not { } catalog) return "No project is open, or Solforge is busy.";
        if (!project.ContainsEdit(tag)) return $"The project does not change {Hex(tag)}; nothing to revert.";
        string? field = McpJson.String(args, "field");
        if (field is not { Length: > 0 })
        {
            workspace.Run(() => workspace.Revert(tag));
            return workspace.Error.Length > 0 ? workspace.Error : $"Reverted {Hex(tag)} to base; undo restores the change.";
        }
        ProjectTagDiff diff;
        try { diff = ProjectTagDiff.Compare(project, tag, catalog); }
        catch (Exception error) when (error is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException) { return error.Message; }
        if (TagBaseDiff.Find(TagFields(tag, diff.Authored, catalog), field) is not { } span)
            return $"No field '{field}' on {Hex(tag)}; compare_with_base lists them, or give a byte range such as 0x40+8.";
        byte[] reverted;
        try { reverted = TagBaseDiff.RevertField(diff.Base, diff.Authored, span); }
        catch (InvalidOperationException error) { return error.Message; }
        if (reverted.AsSpan().SequenceEqual(diff.Authored)) return $"{span.Name} already matches base.";
        if (reverted.AsSpan().SequenceEqual(diff.Base))
        {
            workspace.Run(() => workspace.Revert(tag));
            return workspace.Error.Length > 0 ? workspace.Error : $"{span.Name} was the only change, so {Hex(tag)} is back to base; undo restores it.";
        }
        // ApplyAssetEdits records undo against the open document, as apply_item_edit and replace_texture_pixels do.
        if (workspace.Document?.Entry.TagHash != tag) { workspace.Run(() => workspace.Select(tag)); return "Opened the tag; call revert_to_base again once it shows."; }
        workspace.Run(() => workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [tag] = reverted }, $"Reverted {span.Name} to base"));
        return workspace.Error.Length > 0 ? workspace.Error : $"Reverted {span.Name} ({span.Offset:X}+{span.Size}) of {Hex(tag)} to base; other edits stay; undo restores it.";
    }

    // ---- T1.4 get_item_sources ----

    /** What the item report needs, taken on the UI thread. */
    private sealed record ItemSourceCapture(InvestmentCatalog Data, PackageCatalog Catalog, StudioProject? Project,
        WorldCatalog? World, string? ScriptsPath);

    private async Task<JsonObject> AiItemSourcesAsync(JsonObject args, CancellationToken cancel)
    {
        var (capture, note) = await OnUi(CaptureItemSources).WaitAsync(cancel).ConfigureAwait(false);
        if (capture is null) return AiError(note ?? "The item catalog is not loaded.");
        string? kind = McpJson.String(args, "kind")?.Trim().ToLowerInvariant();
        if (kind is { Length: > 0 } && !SolforgeTools.ItemSourceKinds.Contains(kind))
            return AiError("kind must be one of: " + string.Join(", ", SolforgeTools.ItemSourceKinds));
        var report = await Task.Run(() => ItemSourcesReport(capture, McpJson.String(args, "item") ?? "", kind is { Length: > 0 } ? kind : null,
            McpJson.Bool(args, "deep", true), cancel), cancel).WaitAsync(AiReadTimeout * 2, cancel).ConfigureAwait(false);
        if (report["error"] is not null || report["rows"] is not JsonArray rows) return report;
        report.Remove("rows");
        var page = McpPages.Page(rows.ToArray(), Offset(args), Limit(args), row => row?.DeepClone());
        foreach (var (key, value) in page) report[key] = value?.DeepClone();
        return report;
    }

    /** Takes the catalog references, starting the item catalog load when it has not run. */
    private (ItemSourceCapture? Capture, string? Note) CaptureItemSources()
    {
        if (workspace.Catalog is not { } catalog) return (null, "No installation is open.");
        if (workspace.Investment is not { } data)
        {
            if (!workspace.IsBusy) workspace.Run(workspace.LoadInvestment);
            return (null, "Solforge is reading the item catalog (about a minute the first time); call again shortly.");
        }
        return (new(data, catalog, workspace.Project, workspace.WorldCatalog, settings.ScriptsPath), null);
    }

    /**
     * Every source and use of one item, unpaged: rows of {kind, source, tag, detail, edited}, counts per kind, notes and
     * the truncated scans. Runs off the UI thread.
     */
    private JsonObject ItemSourcesReport(ItemSourceCapture capture, string text, string? kind, bool deep, CancellationToken cancel)
    {
        var (data, catalog, project) = (capture.Data, capture.Catalog, capture.Project);
        if (AiFindItem(data, text) is not { } item) return AiError("No item by that tag, hash or name; use search_items.");
        var edited = project?.Changes.Select(change => change.TagHash).ToHashSet() ?? [];
        var rows = new List<JsonObject>();
        var notes = new List<string>();
        var truncated = new JsonArray();
        bool Want(string k) => kind is null || kind == k;
        void Row(string k, string source, uint? tag, string detail)
        {
            var row = new JsonObject { ["kind"] = k, ["source"] = source, ["detail"] = detail };
            if (tag is { } t) { row["tag"] = Hex(t); if (edited.Contains(t)) row["edited"] = true; }
            rows.Add(row);
        }

        // Loot pools: the server's live loot_pools.lua, then the project's loot_pool content layered on top.
        if (Want("loot_pool"))
        {
            var modules = new List<(JsonNode?, string)>();
            if (capture.ScriptsPath is { Length: > 0 } scripts && Path.Combine(scripts, "loot_pools.lua") is var serverFile && File.Exists(serverFile))
            {
                try { modules.Add((LootPoolsReader.Parse(File.ReadAllText(serverFile)), "server: loot_pools.lua")); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { notes.Add("server loot_pools.lua: " + error.Message); }
            }
            else notes.Add("server loot_pools.lua not found (set Settings > Paths > server scripts; set_settings serverScripts)");
            if (project?.DirectoryPath is { Length: > 0 } root)
            {
                try
                {
                    var pools = new ContentStore(root).LoadAll(ContentFamilies.LootPool).OfType<LootPoolDefinition>().ToArray();
                    if (pools.Length > 0) modules.Add((LootPoolsReader.Parse(ContentExport.LootPoolsModule(pools)), "project: content/loot_pool"));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException) { notes.Add("project loot pools: " + error.Message); }
            }
            bool exoticWeapon = item.Category == InvestmentCategory.Weapons && item.Tier == 5;
            foreach (var hit in LootPoolsReader.Find(modules, item.Hash, catalogName => catalogName == "exotic_weapons" && exoticWeapon))
                Row("loot_pool", $"{hit.Pool} ({hit.List})", null,
                    $"weight {hit.Weight:0.###} of {hit.PoolSize} item(s), x{hit.Quantity}{(hit.Profile ? ", profile" : "")}; {hit.File}; " +
                    (hit.Rules.Count == 0 ? "no enemy_kill rule rolls it (scripts may grant it)" : "rolled by " + string.Join("; ", hit.Rules)));
        }

        // Loot tables: seam for the tag-schemas decoder.
        if (Want("loot_table"))
        {
            // TODO(tag-schemas): when get_loot_table lands, walk the loot-table tags here and add a "loot_table" row per
            // table that drops item.Hash (source: table name, tag; detail: activity, weight, conditions). Until then:
            notes.Add(LootTablesPending);
        }

        // Vendors: sales of the item, and sales that charge it as a cost (catalog.Read carries the project's edits).
        if (Want("vendor") || Want("vendor_cost"))
        {
            var clock = Stopwatch.StartNew();
            int read = 0;
            foreach (var vendor in data.Vendors)
            {
                cancel.ThrowIfCancellationRequested();
                if (clock.Elapsed > ItemScanBudget) { truncated.Add($"vendors: read {read} of {data.Vendors.Count}"); break; }
                InvestmentVendorDetail detail;
                try { detail = data.ReadVendor(vendor.Tag, catalog.Read(vendor.Tag)); read++; }
                catch (Exception error) when (error is InvalidDataException or IOException or KeyNotFoundException or ArgumentOutOfRangeException) { continue; }
                foreach (var sale in detail.Sales)
                {
                    uint owner = sale.OwnerTag != 0 ? sale.OwnerTag : vendor.Tag;
                    if (sale.ItemIndex == item.Index && Want("vendor"))
                        Row("vendor", vendor.Name, owner, $"sale {sale.Index}, category {sale.Category}" + (sale.Costs.Count == 0 ? ", free"
                            : ", costs " + string.Join(", ", sale.Costs.Select(cost => $"{cost.Quantity} x {cost.Name}"))));
                    foreach (var cost in sale.Costs.Where(cost => cost.ItemIndex == item.Index))
                        if (Want("vendor_cost")) Row("vendor_cost", vendor.Name, owner, $"{cost.Quantity} x for sale {sale.Index} ({sale.Name})");
                }
            }
        }

        // Collections: the presentation nodes that list the item's collectible.
        if (Want("collection"))
        {
            try
            {
                var sources = new InvestmentSources();
                if (catalog.ContainsTag(sources.Collectibles) && catalog.ContainsTag(sources.PresentationNodes))
                {
                    int collectible = InvestmentCollections.FindCollectible(catalog.Read(sources.Collectibles), item.Index);
                    if (collectible < 0) notes.Add("collections: the item has no collectible");
                    else foreach (var node in data.CollectionNodes().Where(node => node.Collectibles.Contains(collectible)))
                            Row("collection", node.Name.Length > 0 ? node.Name : $"Node {node.Index}", sources.PresentationNodes,
                                $"collectible {collectible} on page {node.Index} ({node.Hash:X8})");
                }
            }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentOutOfRangeException) { notes.Add("collections: " + error.Message); }
        }

        // Plugs: other items whose sockets start with or offer this item.
        if (Want("plug"))
        {
            var clock = Stopwatch.StartNew();
            int scanned = 0;
            var candidates = data.Items.Where(other => other.Index != item.Index && other.Category is InvestmentCategory.Weapons or InvestmentCategory.Armor).ToArray();
            foreach (var other in candidates)
            {
                cancel.ThrowIfCancellationRequested();
                if (clock.Elapsed > ItemScanBudget) { truncated.Add($"plugs: read {scanned} of {candidates.Length} weapons and armor"); break; }
                InvestmentItemDetail detail;
                try { detail = data.ReadItem(other.Tag, catalog.Read(other.Tag)); scanned++; }
                catch (Exception error) when (error is InvalidDataException or IOException or KeyNotFoundException or ArgumentOutOfRangeException) { continue; }
                foreach (var socket in detail.Sockets)
                    if (socket.InitialPlug == item.Index || socket.AllowedPlugs.Contains((ushort)item.Index))
                        Row("plug", other.Name, other.Tag, $"socket {socket.Index} (type {socket.Type})" + (socket.InitialPlug == item.Index ? ", initial plug" : "")
                            + $", plug set of {socket.AllowedPlugs.Count}");
            }
        }

        // References: activities and the project's edits, walked with TagDependencyGraph (records and quests by name).
        if (deep && (kind is null or "activity" or "record" or "quest" or "reference" or "project_edit"))
            ItemReferences(capture, item, edited, Row, notes, truncated, cancel);

        if (kind is { Length: > 0 }) rows.RemoveAll(row => (string?)row["kind"] != kind);
        var counts = new JsonObject();
        foreach (var group in rows.GroupBy(row => (string?)row["kind"] ?? "").OrderBy(group => group.Key)) counts[group.Key] = group.Count();
        return new JsonObject
        {
            ["item"] = new JsonObject
            {
                ["tag"] = Hex(item.Tag), ["displayTag"] = Hex(item.DisplayTag), ["hash"] = item.Hash, ["index"] = item.Index,
                ["name"] = item.Name, ["type"] = item.TypeName, ["rarity"] = item.Rarity
            },
            ["counts"] = counts, ["lootTables"] = LootTablesPending,
            ["notes"] = new JsonArray(notes.Select(line => (JsonNode)line).ToArray()), ["truncated"] = truncated,
            ["rows"] = new JsonArray(rows.Select(row => (JsonNode)row).ToArray())
        };
    }

    /** Activities and project edits that reach the item, via DependencyManifest and a bounded TagDependencyGraph walk. */
    private void ItemReferences(ItemSourceCapture capture, InvestmentItem item, HashSet<uint> edited,
        Action<string, string, uint?, string> row, List<string> notes, JsonArray truncated, CancellationToken cancel)
    {
        var catalog = capture.Catalog;
        var itemTags = new HashSet<uint> { item.Tag, item.DisplayTag };
        var reported = new HashSet<uint>();
        string Name(uint tag) => workspace.ResolveName(tag) ?? Hex(tag);

        // The project's own edits: the dependency manifest's direct references.
        if (capture.Project is { } project)
        {
            try
            {
                foreach (var node in ItemReferenceScan.ProjectReferences(DependencyCrawler.Crawl(project, catalog), itemTags))
                    if (reported.Add(node.Tag)) row("project_edit", Name(node.Tag), node.Tag, $"{node.Source} {node.Category} tag references the item");
            }
            catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException or KeyNotFoundException)
            { notes.Add("project dependency manifest: " + error.Message); }
        }

        // Activities and edited tags as roots; references are the package's tag words, item hash hits are kept.
        var missions = capture.World?.Missions ?? [];
        var activityNames = missions.GroupBy(mission => mission.Tag).ToDictionary(group => group.Key, group => group.First().Name);
        var roots = missions.Select(mission => mission.Tag).Concat(edited.Order()).Distinct().ToArray();
        if (roots.Length == 0) { notes.Add("references: no activities loaded yet (open the installation's world list) and no project edits"); return; }
        var hashHolders = new Dictionary<uint, byte[]>();
        IEnumerable<uint> References(uint tag)
        {
            if (!catalog.TryGetEntry(tag, out var entry) || !AssetCategories.IsStructured(entry)) return [];
            byte[] bytes;
            try { bytes = catalog.Read(tag); }
            catch (Exception error) when (error is IOException or InvalidDataException or KeyNotFoundException) { return []; }
            var found = new List<uint>();
            bool holdsHash = false;
            for (int offset = 0; offset + 4 <= bytes.Length; offset += 4)
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                if (word == item.Hash) holdsHash = true;
                if (word != tag && word - 0x80800000u < (0x2000u << 13) && catalog.ContainsTag(word)) found.Add(word);
            }
            if (holdsHash) lock (hashHolders) hashHolders[tag] = bytes;
            return found.Distinct();
        }
        var scan = ItemReferenceScan.Scan(roots, References, tag => hashHolders.GetValueOrDefault(tag), itemTags, item.Hash, token: cancel);
        if (scan.Truncated) truncated.Add($"references: stopped at {scan.Visited} tags from {roots.Length} roots");
        foreach (var hit in scan.Hits)
        {
            if (!reported.Add(hit.Tag)) continue;
            string name = Name(hit.Tag);
            string how = hit.Hash ? $"holds the item hash at 0x{hit.Offset:X}" : "references the item tag";
            string via = hit.Via == 0 || hit.Via == hit.Tag ? "" : activityNames.TryGetValue(hit.Via, out var activity)
                ? $" (in activity {activity})" : $" (under {Name(hit.Via)})";
            string lower = name.ToLowerInvariant();
            string kindName = lower.Contains("record") || lower.Contains("triumph") ? "record"
                : lower.Contains("quest") || lower.Contains("bounty") || lower.Contains("pursuit") ? "quest"
                : activityNames.ContainsKey(hit.Tag) || activityNames.ContainsKey(hit.Via) ? "activity"
                : edited.Contains(hit.Tag) || edited.Contains(hit.Via) ? "project_edit" : "reference";
            row(kindName, activityNames.TryGetValue(hit.Tag, out var own) ? own : name, hit.Tag, how + via);
        }
    }
}
