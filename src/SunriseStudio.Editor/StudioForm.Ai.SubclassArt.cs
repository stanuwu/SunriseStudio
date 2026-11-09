/**
 * File: StudioForm partial: the set_icon_layer_texture and set_item_lore MCP actions (run on the UI thread after the
 * approve click; the work is StudioWorkspace.SubclassArt.cs).
 */
using System.Text.Json.Nodes;
using Solforge.Mcp;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** set_icon_layer_texture: a PNG as a new texture for every picture reference of a project icon layer. */
    private string AiSetIconLayerTexture(JsonObject args)
    {
        string name = (McpJson.String(args, "name") ?? "").Trim();
        if (workspace.LastSubclassArt is { } last && last.Key == name && name.Length > 0)
            return last.Summary + ". It is in Project changes with undo.";
        uint layer = SolforgeTools.ParseHash(McpJson.String(args, "layer") ?? "")
            ?? throw new InvalidDataException("Give the icon layer tag (0x...).");
        string path = (McpJson.String(args, "path") ?? "").Trim();
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("Give the absolute path of an existing .png file.");
        if (name.Length is 0 or > 120) throw new InvalidDataException("Give a texture name of 1 to 120 characters.");
        if (!workspace.IsProjectTag(layer))
            throw new InvalidDataException($"{Hex(layer)} is installed; clone the container and layer first (clone_tags).");
        ushort package = workspace.CustomPerkPackage() ?? throw new InvalidDataException("No project package can take the texture.");
        workspace.Run(() => workspace.SetIconLayerTexture(layer, path, name, package));
        return workspace.Error.Length > 0 ? workspace.Error
            : $"Importing {Path.GetFileName(path)} for layer {Hex(layer)}; call set_icon_layer_texture again with the same name "
              + "for the result once get_build_status is idle.";
    }

    /** set_item_lore: an item's own lore row (title, bold subtitle, body) with new project strings. */
    private string AiSetItemLore(JsonObject args)
    {
        string title = (McpJson.String(args, "title") ?? "").Trim();
        if (workspace.LastSubclassArt is { } last && last.Key == title && title.Length > 0 && McpJson.String(args, "body") is null)
            return last.Summary + ". It is in Project changes with undo.";
        uint item = SolforgeTools.ParseHash(McpJson.String(args, "item") ?? "")
            ?? throw new InvalidDataException("Give the item definition tag (0x...).");
        string subtitle = (McpJson.String(args, "subtitle") ?? "").Trim();
        string body = (McpJson.String(args, "body") ?? "").Trim().Replace("\r\n", "\n");
        if (title.Length is 0 or > 200 || subtitle.Length is 0 or > 400 || body.Length is 0 or > 4000)
            throw new InvalidDataException("Give title (1-200), subtitle (1-400) and body (1-4000 characters).");
        if (!workspace.IsProjectTag(item)) throw new InvalidDataException($"{Hex(item)} is installed; only project items can change.");
        ushort package = workspace.CustomPerkPackage() ?? throw new InvalidDataException("No project package can take the strings.");
        workspace.Run(() => workspace.SetItemLore(item, title, subtitle, body, package));
        return workspace.Error.Length > 0 ? workspace.Error
            : $"Writing lore for {Hex(item)}; call set_item_lore again with only item and title for the result once "
              + "get_build_status is idle.";
    }
}

/** fork_list_text_bank and set_talent_display_record (added socket-entry lists only). */
public sealed partial class StudioForm
{
    /** fork_list_text_bank: an added list's own copy of one text bank, with strings replaced, and optional own icons. */
    private string AiForkListTextBank(JsonObject args)
    {
        string key = $"forkbank:{McpJson.String(args, "list")}:{McpJson.String(args, "bank")}";
        if (args["texts"] is null && args["icons"] is null && workspace.LastSubclassArt is { } last && last.Key == key)
            return last.Summary + ". It is in Project changes with undo.";
        if (!int.TryParse(McpJson.String(args, "list") ?? "", out int list) || list < 0)
            throw new InvalidDataException("Give list as the socket-entry list number, e.g. 14.");
        uint bank = SolforgeTools.ParseHash(McpJson.String(args, "bank") ?? "")
            ?? throw new InvalidDataException("Give bank: the installed string bank tag the list names (0x...).");
        var texts = new Dictionary<uint, string>();
        if (args["texts"] is JsonObject textRows)
            foreach (var (hashText, value) in textRows)
            {
                uint hash = SolforgeTools.ParseHash(hashText) ?? throw new InvalidDataException($"'{hashText}' is not a string hash.");
                string text = value?.GetValue<string>()?.Replace("\r\n", "\n") ?? "";
                if (text.Length is 0 or > 4000) throw new InvalidDataException($"Text for 0x{hash:X8} must be 1-4000 characters.");
                texts[hash] = text;
            }
        var icons = new Dictionary<uint, uint>();
        if (args["icons"] is JsonObject iconRows)
            foreach (var (fromText, value) in iconRows)
            {
                uint from = SolforgeTools.ParseHash(fromText) ?? throw new InvalidDataException($"'{fromText}' is not a tag.");
                uint to = SolforgeTools.ParseHash(value?.GetValue<string>() ?? "") ?? throw new InvalidDataException($"Icon for 0x{from:X8} is not a tag.");
                if (!workspace.IsProjectTag(to)) throw new InvalidDataException($"0x{to:X8} is installed; give a project icon container.");
                icons[from] = to;
            }
        if (texts.Count == 0 && icons.Count == 0) throw new InvalidDataException("Give texts ({\"0xHASH\": \"text\"}) and/or icons.");
        ushort package = workspace.CustomPerkPackage() ?? throw new InvalidDataException("No project package can take the bank.");
        workspace.Run(() => workspace.ForkListTextBank(list, bank, texts, icons, package, key));
        return workspace.Error.Length > 0 ? workspace.Error
            : $"Forking list {list}'s bank 0x{bank:X8}; call fork_list_text_bank again with only list and bank for the result "
              + "once get_build_status is idle.";
    }

    /** set_talent_display_record: an added list's display row names another (project) display record. */
    private string AiSetTalentDisplayRecord(JsonObject args)
    {
        var catalog = workspace.Catalog!;
        if (!int.TryParse(McpJson.String(args, "list") ?? "", out int list) || list < 0)
            throw new InvalidDataException("Give list as the socket-entry list number, e.g. 14.");
        uint record = SolforgeTools.ParseHash(McpJson.String(args, "record") ?? "")
            ?? throw new InvalidDataException("Give record: the display record tag (0x...).");
        if (!catalog.TryGetEntry(record, out var entry) || entry.Reference != SunriseStudio.Formats.TalentDisplayTable.DisplayClass)
            throw new InvalidDataException($"0x{record:X8} is not a talent display record (class 0x80805C42).");
        if (!workspace.IsProjectTag(record)) throw new InvalidDataException($"0x{record:X8} is installed; clone it first (clone_tags).");
        uint table = SunriseStudio.Formats.TalentDisplayTable.TableTag;
        int installed = SunriseStudio.Formats.TalentDisplayTable.Count(catalog.ReadOriginal(table));
        byte[] current = catalog.Read(table).ToArray();
        uint before = SunriseStudio.Formats.TalentDisplayTable.ReadRecord(current, list);
        if (before == record) return $"List {list} already names 0x{record:X8}; nothing changed.";
        byte[] next = SunriseStudio.Formats.TalentDisplayTable.SetRecord(current, installed, list, record);
        if (workspace.Document is null)
        {
            workspace.Run(() => workspace.Select(table));
            return "Opened the table; call set_talent_display_record again once it shows.";
        }
        workspace.Run(() => workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [table] = next }, "Set talent display record"));
        if (workspace.Error.Length > 0) return workspace.Error;
        uint after = SunriseStudio.Formats.TalentDisplayTable.ReadRecord(catalog.Read(table), list);
        return after == record
            ? $"List {list} display row: 0x{before:X8} -> 0x{record:X8}. It is in Project changes with undo."
            : $"List {list} did NOT change (still 0x{after:X8}); check Project changes.";
    }
}
