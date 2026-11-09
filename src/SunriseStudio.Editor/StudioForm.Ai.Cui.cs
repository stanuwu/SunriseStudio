using System.Globalization;
using System.Text.Json.Nodes;
using Solforge.Mcp.Data;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/**
 * The MCP CUI tools (get_cui_tree, find_cui_by_widget, render_cui) over the open installation. Runs on the MCP worker: reads go
 * through the catalog's own locks and nothing here touches the UI.
 */
public sealed partial class StudioForm
{
    private sealed partial class AiHost : ISolforgeCuiHost
    {
        private static string H8(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

        private (PackageCatalog Catalog, PackageEntry Entry) CuiEntry(uint tag)
        {
            if (DataCatalog is not { } catalog || !catalog.TryGetEntry(tag, out var entry))
                throw new InvalidDataException($"Unknown tag {H8(tag)}.");
            if (!CuiDocument.Supports(entry.Reference))
                throw new InvalidDataException($"{H8(tag)} is class {H8(entry.Reference)}, not a CUI screen (0x8080496A) or widget resource (0x80804825).");
            return (catalog, entry);
        }

        private string CuiTagName(uint tag) => Owner.workspace.DisplayName(tag);
        private string? CuiHashName(uint hash) => Owner.workspace.ResolveHash(hash);

        private static JsonObject NodeRow(CuiSceneNode node)
        {
            var row = new JsonObject
            {
                ["id"] = node.Id, ["parent"] = node.ParentId < 0 ? null : node.ParentId, ["label"] = node.Label,
                ["type"] = node.TypeName, ["type_key"] = node.TypeKey, ["name_hash"] = H8(node.NameHash),
                ["name"] = node.NameText.Length == 0 ? null : node.NameText,
                ["resource"] = node.Resource == 0 ? null : H8(node.Resource), ["resource_name"] = node.ResourceName.Length == 0 ? null : node.ResourceName,
                ["resource_entry"] = node.EntryOfResource,
                ["layout"] = node.Rectangle is { } r ? new JsonObject { ["x"] = r.X, ["y"] = r.Y, ["w"] = r.Z, ["h"] = r.W } : null,
                ["visible"] = node.Visible, ["hidden_by_static_flag"] = node.HiddenByStaticFlag,
                ["talent_grid"] = node.TalentGrid
            };
            if (node.BitmapTag != 0)
            {
                row["bitmap"] = H8(node.BitmapTag); row["bitmap_name"] = node.BitmapName;
                row["texture"] = node.TextureTag == 0 ? null : H8(node.TextureTag);
            }
            if (node.Tint is { } t) row["tint"] = new JsonArray(t.X, t.Y, t.Z, t.W);
            if (node.Text.Length != 0) row["text"] = node.Text;
            string? art = node.Details.FirstOrDefault(line => line.StartsWith("Art", StringComparison.Ordinal));
            if (art is not null) row["art_status"] = art;
            row["details"] = new JsonArray([.. node.Details.Select(line => (JsonNode?)line)]);
            return row;
        }

        public (JsonObject Header, IReadOnlyList<JsonObject> Rows)? CuiTree(uint tag, int? node, bool includeHidden, CancellationToken cancel)
        {
            if (DataCatalog is not { } catalog || !catalog.TryGetEntry(tag, out _)) return null;
            var (_, entry) = CuiEntry(tag);
            var header = new JsonObject
            {
                ["tag"] = H8(tag), ["name"] = CuiTagName(tag), ["class"] = H8(entry.Reference), ["package"] = entry.PackageName, ["size"] = entry.Size,
                ["note"] = "Static default-profile data; bindings and animations are not evaluated. Node name hashes are stored hashed and rarely resolve."
            };
            if (entry.Reference == 0x80804825)
            {
                var document = CuiDocument.Read(tag, entry.Reference, catalog.Read(tag), CuiHashName, CuiTypeRegistry.BuiltIn);
                var layouts = CuiLayout.Read(document).ToDictionary(item => item.Node.Id, item => item.Rectangle);
                var rows = document.Nodes.Select(item =>
                {
                    string type = CuiWidgetNames.TypeName(document.NativeTypes, item.TypeHash, item.AuxiliaryTypeHash);
                    return new JsonObject
                    {
                        ["id"] = item.Id, ["label"] = $"{item.Id}  {type}", ["type"] = type,
                        ["type_key"] = $"0x{CuiWidgetNames.Key(item.TypeHash, item.AuxiliaryTypeHash):X16}", ["name_hash"] = H8(item.NameHash),
                        ["name"] = CuiHashName(item.NameHash),
                        ["layout"] = layouts.TryGetValue(item.Id, out var r) ? new JsonObject { ["x"] = r.X, ["y"] = r.Y, ["w"] = r.Z, ["h"] = r.W } : null
                    };
                }).ToList();
                header["kind"] = "widget resource (local nodes, no composition)";
                header["imports"] = new JsonArray([.. document.Imports.Select(import => (JsonNode?)new JsonObject
                    { ["resource"] = H8(import.TagHash), ["name"] = CuiTagName(import.TagHash), ["first_node"] = import.FirstNode, ["nodes"] = import.NodeCount })]);
                return (header, rows);
            }
            var preview = CuiPreviewData.Read(catalog, tag, entry.Reference, catalog.Read(tag), CuiHashName, cancel)
                ?? throw new InvalidDataException("This tag is not a CUI screen.");
            if (preview.Composition is null)
                throw new InvalidDataException(preview.Diagnostics.Count > 0 ? string.Join(" ", preview.Diagnostics) : "The screen did not compose.");
            var nodes = CuiSceneInfo.Build(preview, CuiTagName, CuiHashName);
            IEnumerable<CuiSceneNode> selected = nodes;
            if (node is { } start)
            {
                var children = nodes.ToLookup(item => item.ParentId);
                HashSet<int> keep = [];
                var stack = new Stack<int>([start]);
                while (stack.TryPop(out int id)) if (keep.Add(id)) foreach (var child in children[id]) stack.Push(child.Id);
                if (!nodes.Any(item => item.Id == start)) throw new InvalidDataException($"The screen has no node {start}.");
                selected = nodes.Where(item => keep.Contains(item.Id));
            }
            if (!includeHidden) selected = selected.Where(item => item.Visible);
            var list = selected.ToList();
            header["kind"] = "screen (composed)"; header["root_resource"] = H8(preview.Document!.RootResource);
            header["nodes_total"] = nodes.Count;
            header["with_art"] = nodes.Count(item => item.TextureTag != 0);
            header["art_unresolved"] = nodes.Count(item => item.TypeName == "Bitmap" && item.TextureTag == 0);
            header["talent_grid_nodes"] = new JsonArray([.. nodes.Where(item => item.TalentGrid).Select(item => (JsonNode?)item.Id)]);
            return (header, list.Select(NodeRow).ToList());
        }

        public (JsonObject Header, IReadOnlyList<JsonObject> Rows) FindCuiByWidget(ulong? typeKey, uint? typeLow, uint? nameHash, CancellationToken cancel)
        {
            var catalog = DataCatalog ?? throw new InvalidDataException("No game installation is open in Solforge yet.");
            bool Matches(CuiNode item) =>
                (typeKey is null && typeLow is null || typeKey is { } key && CuiWidgetNames.Key(item.TypeHash, item.AuxiliaryTypeHash) == key
                    || typeLow is { } low && item.TypeHash == low)
                && (nameHash is null || item.NameHash == nameHash);
            Dictionary<uint, CuiDocument?> resources = [];
            CuiDocument? Resource(uint tag)
            {
                if (resources.TryGetValue(tag, out var known)) return known;
                CuiDocument? document = null;
                try
                {
                    if (catalog.TryGetEntry(tag, out var entry) && entry.Reference == 0x80804825)
                        document = CuiDocument.Read(tag, entry.Reference, catalog.Read(tag), null, CuiTypeRegistry.BuiltIn);
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentOutOfRangeException or KeyNotFoundException) { }
                resources[tag] = document;
                return document;
            }
            IEnumerable<CuiDocument> Closure(uint root)
            {
                HashSet<uint> seen = [];
                var stack = new Stack<uint>([root]);
                while (stack.TryPop(out uint tag))
                {
                    if (!seen.Add(tag) || seen.Count > 5120 || Resource(tag) is not { } document) continue;
                    yield return document;
                    foreach (var import in document.Imports) stack.Push(import.TagHash);
                }
            }
            List<JsonObject> rows = [];
            int scanned = 0;
            foreach (var entry in catalog.Entries.Where(item => item.Reference is 0x80804825 or 0x8080496A).OrderBy(item => item.TagHash))
            {
                cancel.ThrowIfCancellationRequested();
                scanned++;
                int count = 0;
                List<string> suppliers = [];
                if (entry.Reference == 0x80804825)
                {
                    if (Resource(entry.TagHash) is { } own) count = own.Nodes.Count(Matches);
                }
                else
                {
                    CuiDocument screen;
                    try { screen = CuiDocument.Read(entry.TagHash, entry.Reference, catalog.Read(entry.TagHash), null, CuiTypeRegistry.BuiltIn); }
                    catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentOutOfRangeException) { continue; }
                    foreach (var document in Closure(screen.RootResource))
                    {
                        int own = document.Nodes.Count(Matches);
                        if (own == 0) continue;
                        count += own; suppliers.Add(H8(document.TagHash));
                    }
                }
                if (count == 0) continue;
                var row = new JsonObject
                {
                    ["tag"] = H8(entry.TagHash), ["name"] = CuiTagName(entry.TagHash), ["class"] = H8(entry.Reference),
                    ["kind"] = entry.Reference == 0x8080496A ? "screen" : "resource", ["package"] = entry.PackageName, ["matching_nodes"] = count
                };
                if (suppliers.Count > 0) row["supplied_by"] = new JsonArray([.. suppliers.Take(8).Select(item => (JsonNode?)item)]);
                rows.Add(row);
            }
            var header = new JsonObject
            {
                ["scanned"] = scanned, ["type"] = typeKey is { } k ? $"0x{k:X16}" : typeLow is { } l ? H8(l) : null,
                ["name_hash"] = nameHash is { } n ? H8(n) : null,
                ["note"] = "Screens match through the resources they import (a resource imported twice counts once)."
            };
            return (header, rows.OrderByDescending(row => (int)row["matching_nodes"]!).ThenBy(row => (string)row["tag"]!, StringComparer.Ordinal).ToList());
        }

        public JsonObject? RenderCui(uint tag, int? node, uint? subclassItem, float scale, bool labels, bool showHidden, string folder, CancellationToken cancel)
        {
            if (DataCatalog is not { } catalog || !catalog.TryGetEntry(tag, out _)) return null;
            var (_, entry) = CuiEntry(tag);
            if (entry.Reference != 0x8080496A) throw new InvalidDataException("render_cui draws screens (0x8080496A). Open a widget resource through a screen that imports it.");
            var preview = CuiPreviewData.Read(catalog, tag, entry.Reference, catalog.Read(tag), CuiHashName, cancel)
                ?? throw new InvalidDataException("This tag is not a CUI screen.");
            if (preview.Composition is null)
                throw new InvalidDataException(preview.Diagnostics.Count > 0 ? string.Join(" ", preview.Diagnostics) : "The screen did not compose.");
            var nodes = CuiSceneInfo.Build(preview, CuiTagName, CuiHashName);
            IReadOnlyList<CuiSubclassSlot>? overlay = null;
            var notes = new JsonArray();
            if (subclassItem is { } item)
            {
                string? tsv = SubclassNodesTsv();
                if (tsv is null) throw new InvalidDataException("subclass_nodes.tsv was not found under <project>\\build\\server\\data. Build or export the project first.");
                var panel = nodes.FirstOrDefault(value => value.TalentGrid)?.Rectangle ?? new(102, 108, CuiSubclassGrid.PanelWidth, CuiSubclassGrid.PanelHeight);
                overlay = CuiSubclassGrid.Slots(tsv, item, panel);
                if (!CuiSubclassGrid.Items(tsv).Any(row => row.Item == item)) notes.Add($"No rows for item {H8(item)} in subclass_nodes.tsv; slots are unnamed.");
                notes.Add("Subclass grid slot positions are diamonds placed from a native screenshot, not read from the CUI data.");
            }
            var result = CuiSceneRenderer.Render(preview, nodes, new(node, scale, overlay, labels, showHidden));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"{tag:X8}{(node is { } n ? $"-node{n}" : "")}{(subclassItem is { } s ? $"-{s:X8}" : "")}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            File.WriteAllBytes(path, result.Png);
            return new JsonObject
            {
                ["path"] = path, ["tag"] = H8(tag), ["name"] = CuiTagName(tag), ["width"] = result.Width, ["height"] = result.Height,
                ["nodes_drawn"] = result.Drawn, ["with_art"] = result.WithArt, ["placeholders"] = result.Placeholders,
                ["hidden_skipped"] = result.Hidden, ["notes"] = notes
            };
        }

        /** subclass_nodes.tsv of the open project's server build, or null. */
        private string? SubclassNodesTsv()
        {
            if (Owner.workspace.Project?.DirectoryPath is not { } root) return null;
            string path = Path.Combine(root, "build", "server", "data", "subclass_nodes.tsv");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
