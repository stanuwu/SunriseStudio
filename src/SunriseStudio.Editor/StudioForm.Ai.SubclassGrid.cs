/**
 * File: StudioForm partial: the fork_subclass_grid MCP action (runs on the UI thread after the approve click).
 */
using System.Text.Json.Nodes;
using Solforge.Mcp;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** fork_subclass_grid: gives a subclass item its own socket-entry list and pools (the talent-grid writer). */
    private string AiForkSubclassGrid(JsonObject args)
    {
        string name = (McpJson.String(args, "name") ?? "").Trim();
        if (workspace.LastSubclassGrid is { } last && last.Name == name)
            return $"Forked as {name}: item {Hex(last.ItemTag)} now names list {last.NewList} (was {last.OldList}); "
                + $"list definition {Hex(last.DefinitionTag)}; copies: "
                + string.Join(", ", last.Tags.Select(pair => $"{Hex(pair.Key)} -> {Hex(pair.Value)}"))
                + ". It is in Project changes with undo.";
        uint item = SolforgeTools.ParseHash(McpJson.String(args, "item") ?? "")
            ?? throw new InvalidDataException("Give the subclass item tag (0x...).");
        ushort package = workspace.CustomPerkPackage() ?? throw new InvalidDataException("No project package can take the copies.");
        workspace.Run(() => workspace.ForkSubclassGrid(item, name, package));
        return workspace.Error.Length > 0 ? workspace.Error
            : $"Forking {Hex(item)}'s socket-entry list into package {package:X4}; call fork_subclass_grid again with the same "
              + "name for the new list once get_build_status is idle.";
    }

    /** Display +0x80 (primary) and +0x82 (secondary, the tooltip element icon) are u16 rows of the icon table. */
    private const int DisplayPrimaryIcon = 0x80, DisplaySecondaryIcon = 0x82;

    /**
     * set_display_icons: points a project item display's icon rows (+0x80 primary, +0x82 secondary) at existing
     * icon-table rows. Only displays the project owns; rows must exist in the icon table. One undo step.
     */
    private string AiSetDisplayIcons(JsonObject args)
    {
        var catalog = workspace.Catalog!;
        uint display = SolforgeTools.ParseHash(McpJson.String(args, "display") ?? "")
            ?? throw new InvalidDataException("Give the item display tag (0x...).");
        if (!catalog.TryGetEntry(display, out var entry) || entry.Reference != SunriseStudio.Formats.InvestmentCatalog.DisplayClass)
            throw new InvalidDataException($"{Hex(display)} is not an item display tag.");
        if (!workspace.IsProjectTag(display))
            throw new InvalidDataException($"{Hex(display)} is installed; only displays the project owns can change.");
        ushort? Row(string key)
        {
            string? text = McpJson.String(args, key);
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            return ushort.Parse(hex ? text[2..] : text, hex ? System.Globalization.NumberStyles.HexNumber
                : System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        }
        ushort? primary = Row("primary"), secondary = Row("secondary");
        if (primary is null && secondary is null) throw new InvalidDataException("Give primary and/or secondary (icon row).");
        int rows = SunriseStudio.Formats.InvestmentItemClone.ReadIndexRows(
            catalog.Read(new SunriseStudio.Formats.InvestmentSources().Icons)).Count;
        foreach (var row in new[] { primary, secondary })
            if (row is { } value && value >= rows)
                throw new InvalidDataException($"Icon row {value} (0x{value:X}) is outside the icon table (0..{rows - 1}).");
        byte[] bytes = catalog.Read(display).ToArray();
        if (bytes.Length < DisplaySecondaryIcon + 2) throw new InvalidDataException($"{Hex(display)} is truncated.");
        ushort oldPrimary = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(DisplayPrimaryIcon));
        ushort oldSecondary = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(DisplaySecondaryIcon));
        if (primary is { } p) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(DisplayPrimaryIcon), p);
        if (secondary is { } s) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(DisplaySecondaryIcon), s);
        // ApplyAssetEdits records undo against the open document, as edit_perk_behavior does.
        if (workspace.Document is null)
        {
            workspace.Run(() => workspace.Select(display));
            return "Opened the display; call set_display_icons again once it shows.";
        }
        workspace.Run(() => workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [display] = bytes }, "Set display icons"));
        if (workspace.Error.Length > 0) return workspace.Error;
        return $"Display {Hex(display)}: +0x80 0x{oldPrimary:X4} -> 0x{(primary ?? oldPrimary):X4}, "
            + $"+0x82 0x{oldSecondary:X4} -> 0x{(secondary ?? oldSecondary):X4}. It is in Project changes with undo.";
    }

    /** Item definition +0x1FC (u32): the subclass screen art index (base subclasses: Arc 97, Solar 98, Void 99). */
    private const int ItemScreenArtIndex = 0x1FC;
    private static readonly uint[] ScreenArtValues = [97, 98, 99];

    /**
     * set_item_screen_art: writes the screen art index of a project-owned item definition. Only the values the base
     * subclass items use are accepted, and only when the current value is already one of them (so the offset is
     * known to hold this field). One undo step.
     */
    private string AiSetItemScreenArt(JsonObject args)
    {
        var catalog = workspace.Catalog!;
        uint item = SolforgeTools.ParseHash(McpJson.String(args, "item") ?? "")
            ?? throw new InvalidDataException("Give the item definition tag (0x...).");
        if (!catalog.TryGetEntry(item, out var entry) || entry.Reference != SunriseStudio.Formats.InvestmentCatalog.ItemClass)
            throw new InvalidDataException($"{Hex(item)} is not an item definition tag.");
        if (!workspace.IsProjectTag(item))
            throw new InvalidDataException($"{Hex(item)} is installed; only items the project owns can change.");
        if (!uint.TryParse((McpJson.String(args, "value") ?? "").Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out uint value) || Array.IndexOf(ScreenArtValues, value) < 0)
            throw new InvalidDataException("Value must be 97 (Arc), 98 (Solar) or 99 (Void).");
        byte[] bytes = catalog.Read(item).ToArray();
        if (bytes.Length < ItemScreenArtIndex + 4) throw new InvalidDataException($"{Hex(item)} is too short for +0x1FC.");
        uint old = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(ItemScreenArtIndex));
        if (Array.IndexOf(ScreenArtValues, old) < 0)
            throw new InvalidDataException($"{Hex(item)} +0x1FC holds {old}, not a subclass screen art index; nothing changed.");
        if (old == value) return $"Item {Hex(item)} +0x1FC is already {value}.";
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(ItemScreenArtIndex), value);
        if (workspace.Document is null)
        {
            workspace.Run(() => workspace.Select(item));
            return "Opened the item; call set_item_screen_art again once it shows.";
        }
        workspace.Run(() => workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [item] = bytes }, "Set item screen art index"));
        if (workspace.Error.Length > 0) return workspace.Error;
        return $"Item {Hex(item)}: +0x1FC {old} -> {value}. It is in Project changes with undo.";
    }

    /**
     * add_list_presentation_rows: appends rows to the dense item-presentation table's per-list array (0x81A28B23
     * descriptor +0x48), each a copy of its base list's row, for socket-entry lists a grid fork added. The client reads
     * that row by list index; without it the subclass screen shows no nodes. Rows must follow the existing ones in order
     * and the list must exist in the socket-entry-list table. One undo step.
     */
    private string AiAddListPresentationRows(JsonObject args)
    {
        var catalog = workspace.Catalog!;
        var rows = ParseListRows(args);
        uint table = SunriseStudio.Formats.InvestmentItemArt.TableTag;
        byte[] current = catalog.Read(table).ToArray();
        int existing = SunriseStudio.Formats.InvestmentItemArt.ListCount(current);
        if (rows.All(row => row.List < existing))
            return $"The table already has {existing} list rows; nothing changed.";
        int lists = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            catalog.Read(Creation.SubclassGridAuthoring.SocketEntryListTable).AsSpan(8, 4));
        foreach (var (list, _) in rows)
            if (list >= lists)
                throw new InvalidDataException($"List {list} is not in the socket-entry-list table ({lists} lists).");
        var pending = rows.Where(row => row.List >= existing).OrderBy(row => row.List).ToList();
        byte[] appended = SunriseStudio.Formats.InvestmentItemArt.AppendListRows(current, pending);
        byte[] rebuilt = SunriseStudio.Formats.InvestmentItemArt.Rebuild(catalog.ReadOriginal(table), appended);
        if (workspace.Document is null)
        {
            workspace.Run(() => workspace.Select(table));
            return "Opened the table; call add_list_presentation_rows again once it shows.";
        }
        workspace.Run(() => workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [table] = rebuilt }, "Add list presentation rows"));
        if (workspace.Error.Length > 0) return workspace.Error;
        var after = catalog.Read(table);
        var lines = pending.Select(row =>
        {
            var (first, count) = SunriseStudio.Formats.InvestmentItemArt.ReadListRow(after, row.List);
            var (baseFirst, baseCount) = SunriseStudio.Formats.InvestmentItemArt.ReadListRow(after, row.Base);
            return $"list {row.List} = {{{first}, {count}}} (copy of list {row.Base} {{{baseFirst}, {baseCount}}})";
        });
        return $"Table {Hex(table)} now has {SunriseStudio.Formats.InvestmentItemArt.ListCount(after)} list rows: "
            + string.Join("; ", lines) + ". It is in Project changes with undo.";
    }

    /** rows: "list=base" pairs (decimal list indices), e.g. 14=3,15=5,16=11. */
    private static List<(int List, int Base)> ParseListRows(JsonObject args)
    {
        string text = (McpJson.String(args, "rows") ?? "").Trim();
        var rows = new List<(int List, int Base)>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = part.Split('=', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !int.TryParse(pair[0], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int list)
                || !int.TryParse(pair[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int source))
                throw new InvalidDataException($"'{part}' is not list=base (decimal list indices).");
            rows.Add((list, source));
        }
        if (rows.Count == 0) throw new InvalidDataException("Give rows as list=base pairs, e.g. 14=3,15=5,16=11.");
        return rows;
    }

    /**
     * add_talent_display_rows: appends rows to the client talent display table (0x81613D09, class 0x80805C3C) for
     * socket-entry lists a grid fork added. Each row takes the list's hash from the socket-entry-list table and shares its
     * base list's display record (class 0x80805C42). Without the row the subclass screen draws an empty grid. One undo step.
     */
    private string AiAddTalentDisplayRows(JsonObject args)
    {
        var catalog = workspace.Catalog!;
        var rows = ParseListRows(args);
        uint table = SunriseStudio.Formats.TalentDisplayTable.TableTag;
        if (!catalog.TryGetEntry(table, out var entry) || entry.Reference != SunriseStudio.Formats.TalentDisplayTable.TableClass)
            throw new InvalidDataException($"{Hex(table)} is not the talent display table (class 0x80805C3C).");
        byte[] current = catalog.Read(table).ToArray();
        var (next, added) = SunriseStudio.Formats.TalentDisplayTable.AppendRows(current,
            catalog.Read(Creation.SubclassGridAuthoring.SocketEntryListTable), rows);
        if (added.Count == 0)
            return $"The talent display table already has {SunriseStudio.Formats.TalentDisplayTable.Count(current)} rows; nothing changed.";
        if (workspace.Document is null)
        {
            workspace.Run(() => workspace.Select(table));
            return "Opened the table; call add_talent_display_rows again once it shows.";
        }
        workspace.Run(() => workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [table] = next }, "Add talent display rows"));
        if (workspace.Error.Length > 0) return workspace.Error;
        var after = SunriseStudio.Formats.InvestmentItemClone.ReadIndexRows(catalog.Read(table));
        var lines = added.Select(row => after.Count > row.List && after[row.List].Hash == row.Hash && after[row.List].Tag == row.Display
            ? $"list {row.List} = {{0x{row.Hash:X8}, {Hex(row.Display)}}}"
            : $"list {row.List} did NOT land; check Project changes");
        return $"Talent display table {Hex(table)} now has {after.Count} rows: " + string.Join("; ", lines)
            + ". It is in Project changes with undo.";
    }

    /** Icon containers (class 0x80804A53) the project owns; anything else is refused. */
    private uint RequireProjectIconContainer(JsonObject args)
    {
        var catalog = workspace.Catalog!;
        uint container = SolforgeTools.ParseHash(McpJson.String(args, "container") ?? "")
            ?? throw new InvalidDataException("Give the icon container tag (0x...).");
        if (!catalog.TryGetEntry(container, out var entry) || entry.Reference != 0x80804A53)
            throw new InvalidDataException($"{Hex(container)} is not an icon container (class 0x80804A53).");
        if (!workspace.IsProjectTag(container))
            throw new InvalidDataException($"{Hex(container)} is installed; only icon containers the project owns are allowed.");
        return container;
    }

    /**
     * add_icon_row: appends an icon-table row naming a project icon container, through the custom-icon row writer, under
     * a hash derived from the container tag. A row already naming the container is returned unchanged. One undo step.
     */
    private string AiAddIconRow(JsonObject args)
    {
        var catalog = workspace.Catalog!;
        uint container = RequireProjectIconContainer(args);
        uint table = new SunriseStudio.Formats.InvestmentSources().Icons;
        byte[] icons = catalog.Read(table).ToArray();
        var rows = SunriseStudio.Formats.InvestmentItemClone.ReadIndexRows(icons);
        if (rows.FirstOrDefault(row => row.Tag == container) is { } existing)
            return $"Icon row {existing.Index} (0x{existing.Index:X4}) already names {Hex(container)}; nothing changed.";
        uint hash = SunriseStudio.Formats.SubclassPresentationEdits.IconRowHash(container);
        if (rows.Any(row => row.Hash == hash))
            throw new InvalidDataException($"Icon-table hash 0x{hash:X8} is already used by another row; nothing changed.");
        int installed = SunriseStudio.Formats.InvestmentItemClone.ReadIndexRows(catalog.ReadOriginal(table)).Count;
        var added = SunriseStudio.Formats.InvestmentIconEdits.SetItemRow(icons, hash, container, installed);
        if (workspace.Document is null)
        {
            workspace.Run(() => workspace.Select(container));
            return "Opened the container; call add_icon_row again once it shows.";
        }
        workspace.Run(() => workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [table] = added.Table }, "Add icon row"));
        if (workspace.Error.Length > 0) return workspace.Error;
        var after = SunriseStudio.Formats.InvestmentItemClone.ReadIndexRows(catalog.Read(table));
        if (added.Index >= after.Count || after[added.Index].Tag != container)
            return $"The icon table did not take the row for {Hex(container)}; check Project changes.";
        return $"Icon row {added.Index} (0x{added.Index:X4}) -> container {Hex(container)} (row hash 0x{hash:X8}); the table has "
            + $"{after.Count} rows. It is in Project changes with undo.";
    }

    /**
     * set_icon_container_fingerprint: writes +0x10 of a project icon container. Default: Parhelion's SHA-1 form over the
     * container from +0x14, its tag and its layer's bytes, so a copied container stops sharing its donor's value.
     */
    private string AiSetIconContainerFingerprint(JsonObject args)
    {
        var catalog = workspace.Catalog!;
        uint container = RequireProjectIconContainer(args);
        byte[] bytes = catalog.Read(container).ToArray();
        if (bytes.Length < SunriseStudio.Formats.SubclassPresentationEdits.ContainerLayerOffset + 4)
            throw new InvalidDataException($"{Hex(container)} is truncated.");
        uint old = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(SunriseStudio.Formats.SubclassPresentationEdits.ContainerFingerprintOffset));
        uint value;
        string? text = McpJson.String(args, "value");
        if (!string.IsNullOrWhiteSpace(text))
        {
            text = text.Trim();
            bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!uint.TryParse(hex ? text[2..] : text, hex ? System.Globalization.NumberStyles.HexNumber
                    : System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
                throw new InvalidDataException("value must be a u32 (decimal or 0x hex).");
        }
        else
        {
            uint layer = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(SunriseStudio.Formats.SubclassPresentationEdits.ContainerLayerOffset));
            byte[] layerBytes = layer is not (0 or uint.MaxValue) && catalog.ContainsTag(layer) ? catalog.Read(layer).ToArray() : [];
            value = SunriseStudio.Formats.SubclassPresentationEdits.Fingerprint(bytes, container, layerBytes);
        }
        if (value == old) return $"Container {Hex(container)} +0x10 is already 0x{value:X8}.";
        byte[] edited = SunriseStudio.Formats.SubclassPresentationEdits.SetFingerprint(bytes, value);
        if (workspace.Document is null)
        {
            workspace.Run(() => workspace.Select(container));
            return "Opened the container; call set_icon_container_fingerprint again once it shows.";
        }
        workspace.Run(() => workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [container] = edited }, "Set icon container fingerprint"));
        if (workspace.Error.Length > 0) return workspace.Error;
        return $"Container {Hex(container)}: +0x10 0x{old:X8} -> 0x{value:X8}. It is in Project changes with undo.";
    }

    /**
     * set_subclass_element: the definition's element flag and element value (+0x90 unlock block) and the display's stat
     * block damage type (+0x70 -> +8), as Parhelion's set_element and set_damage_type write them. Project items only.
     */
    private string AiSetSubclassElement(JsonObject args)
    {
        var catalog = workspace.Catalog!;
        uint item = SolforgeTools.ParseHash(McpJson.String(args, "item") ?? "")
            ?? throw new InvalidDataException("Give the subclass item definition tag (0x...).");
        var element = SunriseStudio.Formats.SubclassPresentationEdits.ParseElement(McpJson.String(args, "element") ?? "");
        if (!catalog.TryGetEntry(item, out var entry) || entry.Reference != SunriseStudio.Formats.InvestmentCatalog.ItemClass)
            throw new InvalidDataException($"{Hex(item)} is not an item definition tag.");
        if (!workspace.IsProjectTag(item))
            throw new InvalidDataException($"{Hex(item)} is installed; only items the project owns can change.");
        var sources = new SunriseStudio.Formats.InvestmentSources();
        var items = SunriseStudio.Formats.InvestmentItemClone.ReadIndexRows(catalog.Read(sources.Items));
        var displays = SunriseStudio.Formats.InvestmentItemClone.ReadIndexRows(catalog.Read(sources.Displays));
        var row = items.FirstOrDefault(candidate => candidate.Tag == item)
            ?? throw new InvalidDataException($"{Hex(item)} has no item-table row.");
        if (row.Index >= displays.Count) throw new InvalidDataException($"{Hex(item)} has no paired display row.");
        uint display = displays[row.Index].Tag;
        if (!workspace.IsProjectTag(display))
            throw new InvalidDataException($"Display {Hex(display)} is installed; only displays the project owns can change.");
        byte[] definition = catalog.Read(item).ToArray(), strings = catalog.Read(display).ToArray();
        var oldElement = SunriseStudio.Formats.SubclassPresentationEdits.ReadElement(definition);
        uint oldDamage = SunriseStudio.Formats.SubclassPresentationEdits.ReadDamageType(strings);
        byte[] newDefinition = oldElement == element ? definition
            : SunriseStudio.Formats.SubclassPresentationEdits.SetElement(definition, element);
        byte[] newStrings = SunriseStudio.Formats.SubclassPresentationEdits.SetDamageType(strings, element);
        if (newDefinition.AsSpan().SequenceEqual(definition) && newStrings.AsSpan().SequenceEqual(strings))
            return $"Item {Hex(item)} is already {element}.";
        if (workspace.Document is null)
        {
            workspace.Run(() => workspace.Select(item));
            return "Opened the item; call set_subclass_element again once it shows.";
        }
        workspace.Run(() => workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [item] = newDefinition, [display] = newStrings },
            "Set subclass element"));
        if (workspace.Error.Length > 0) return workspace.Error;
        return $"Item {Hex(item)}: element {oldElement?.ToString() ?? "none"} -> {element} (unlock flag + value 0x1E9); display "
            + $"{Hex(display)} stat block damage type {oldDamage} -> {SunriseStudio.Formats.SubclassPresentationEdits.DamageCode(element)}. "
            + "It is in Project changes with undo.";
    }
}
