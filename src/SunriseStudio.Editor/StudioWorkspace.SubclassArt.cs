/**
 * File: StudioWorkspace partial: a new texture for a project icon layer (set_icon_layer_texture) and an item's own lore
 * row (set_item_lore). Both plan off-thread and commit as one undoable transaction (CommitPlan).
 */
using System.Buffers.Binary;
using SunriseStudio.Authoring;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** What set_icon_layer_texture or set_item_lore did, for the follow-up reply. */
public sealed record PreparedSubclassArt(string Key, string Summary, IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<uint, byte[]> Changes);

public sealed partial class StudioWorkspace
{
    /** The last set_icon_layer_texture / set_item_lore result. */
    public PreparedSubclassArt? LastSubclassArt { get; private set; }

    /**
     * Imports a PNG as a new 2D RGBA8 texture in the project package and points every texture reference of a project icon
     * layer (class 0x80804A69, s_icon_bitmap) at it. The layer's first installed texture is the header template.
     */
    public void SetIconLayerTexture(uint layer, string png, string name, ushort packageId)
    {
        LastSubclassArt = null;
        CommitPlan("Importing icon layer texture", (project, catalog) =>
        {
            using var plan = new ExoticWeaponAuthoring.PlanCatalog(catalog, project, packageId);
            var planned = plan.Catalog;
            if (!planned.TryGetEntry(layer, out var layerEntry) || layerEntry.Reference != 0x80804A69)
                throw new InvalidDataException($"0x{layer:X8} is not an icon layer (class 0x80804A69).");
            byte[] bytes = planned.Read(layer);
            var offsets = new List<int>();
            uint? template = null;
            var replaced = new SortedSet<uint>();
            for (int at = 0x10; at + 4 <= bytes.Length; at += 4)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                if (value is 0 or uint.MaxValue || !planned.TryGetEntry(value, out var entry)
                    || entry.Type != 32 || entry.SubType != 1 || entry.Size != 40) continue;
                offsets.Add(at);
                replaced.Add(value);
                if (template is null && planned.ContainsInstalledPackage(entry.PackageId)) template = value;
            }
            if (offsets.Count == 0) throw new InvalidDataException($"Layer 0x{layer:X8} names no texture.");
            var texture = TextureAssetImporter.PrepareRgba(planned, PngInterchange.Read(png), name, packageId, plan.NextEntry, template);
            plan.Add(texture.Assets);
            foreach (int at in offsets) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), texture.HeaderTag);
            plan.Edit(new Dictionary<uint, byte[]> { [layer] = bytes });
            string summary = $"Layer 0x{layer:X8}: {offsets.Count} texture reference(s) "
                + $"({string.Join(", ", replaced.Select(tag => $"0x{tag:X8}"))}) -> new texture 0x{texture.HeaderTag:X8} "
                + $"(pixels 0x{texture.PayloadTag:X8}, {texture.Width}x{texture.Height} RGBA8, header template "
                + (template is { } t ? $"0x{t:X8}" : "auto") + ")";
            return new PreparedSubclassArt(name, summary, plan.Assets.ToArray(), plan.Changes.ToDictionary());
        }, plan => plan.Assets, plan => plan.Changes, plan =>
        {
            LastSubclassArt = plan;
            return plan.Summary;
        });
    }

    /**
     * Gives an item its own lore row: title, bold subtitle and body as new project strings, a new row appended to both lore
     * tables (copied from the row the item names now) and the item's lore block pointed at it. When the item already names
     * a row the project appended, that row's strings are replaced instead of adding another.
     */
    public void SetItemLore(uint item, string title, string subtitle, string body, ushort packageId)
    {
        LastSubclassArt = null;
        CommitPlan("Writing item lore", (project, catalog) =>
        {
            using var plan = new ExoticWeaponAuthoring.PlanCatalog(catalog, project, packageId);
            var planned = plan.Catalog;
            if (!planned.TryGetEntry(item, out var itemEntry) || itemEntry.Reference != InvestmentTags.ItemClass)
                throw new InvalidDataException($"0x{item:X8} is not an item definition.");
            var texts = new[] { title, subtitle, body }.Distinct(StringComparer.Ordinal)
                .Select(text => new StringBankText(StringBankWriter.Hash(text), text)).ToArray();
            var banks = ProjectStringBank.Plan(planned, project, packageId, plan.NextEntry, texts, [], new());
            plan.Add(banks.Additions);
            plan.Edit(banks.Edits);
            byte[] definitions = planned.Read(LoreTables.DefinitionsTag), display = planned.Read(LoreTables.DisplayTag);
            byte[] itemBytes = planned.Read(item);
            var hashes = LoreTables.RowHashes(definitions, display);
            int installed = LoreTables.RowHashes(planned.ReadOriginal(LoreTables.DefinitionsTag),
                planned.ReadOriginal(LoreTables.DisplayTag)).Count;
            var current = LoreTables.ReadItemLore(itemBytes) ?? throw new InvalidDataException("The item has no lore block.");
            uint bank = checked((uint)banks.BankIndex);
            uint titleHash = StringBankWriter.Hash(title), subtitleHash = StringBankWriter.Hash(subtitle), bodyHash = StringBankWriter.Hash(body);
            string summary;
            if (current.Index >= installed && current.Index < hashes.Count)
            {
                byte[] edited = LoreTables.SetReferences(display, current.Index, bank, titleHash, subtitleHash, bodyHash);
                plan.Edit(new Dictionary<uint, byte[]> { [LoreTables.DisplayTag] = edited });
                summary = $"Item 0x{item:X8} lore row {current.Index} (project row) strings replaced";
            }
            else
            {
                int template = current.Index < hashes.Count ? current.Index : 0;
                uint rowHash = InvestmentItemClone.NewDefinitionHash($"sunrise:lore:{project.Name}:{item:X8}", hashes.ToHashSet());
                var result = LoreTables.AddItemLore(definitions, display, itemBytes, template, rowHash, bank,
                    titleHash, subtitleHash, bodyHash);
                plan.Edit(new Dictionary<uint, byte[]>
                {
                    [LoreTables.DefinitionsTag] = result.Definitions, [LoreTables.DisplayTag] = result.Display, [item] = result.Item
                });
                summary = $"Item 0x{item:X8} lore {current.Index} -> new row {result.Index} (hash 0x{rowHash:X8}, copied from row {template})";
            }
            summary += $"; strings in bank {bank}: title 0x{titleHash:X8}, subtitle 0x{subtitleHash:X8}, body 0x{bodyHash:X8}";
            return new PreparedSubclassArt(title, summary, plan.Assets.ToArray(), plan.Changes.ToDictionary());
        }, plan => plan.Assets, plan => plan.Changes, plan =>
        {
            LastSubclassArt = plan;
            return plan.Summary;
        });
    }
}

/** fork_list_text_bank: one added socket-entry list's own text bank (and optionally its own node icons). */
public sealed partial class StudioWorkspace
{
    /**
     * Gives an added list its own copy of one of its text banks (class 0x80809A88, named by the list's type-9 field), with
     * some strings replaced or added, plus a bank index row so node records can name it; then the list's presentation
     * fields are forked so only this list reads the copy (and, when icons are given, its own icon containers). The base
     * list and every shipped list keep their banks and icons. Re-running for the same list and bank rewrites the copy.
     * @param texts Role hash to new text (e.g. 0xDF417340 = the path name).
     * @param icons Installed icon handle to project container, for the list's type-1 icon field.
     */
    public void ForkListTextBank(int list, uint bank, IReadOnlyDictionary<uint, string> texts, IReadOnlyDictionary<uint, uint> icons,
        ushort packageId, string key)
    {
        LastSubclassArt = null;
        CommitPlan("Forking list text bank", (project, catalog) =>
        {
            using var plan = new ExoticWeaponAuthoring.PlanCatalog(catalog, project, packageId);
            var planned = plan.Catalog;
            uint artTag = InvestmentItemArt.TableTag;
            byte[] art = planned.Read(artTag);
            int installedLists = InvestmentItemArt.ListCount(planned.ReadOriginal(artTag));
            if (list < installedLists) throw new InvalidDataException($"List {list} is shipped; only added lists can change.");
            var handles = InvestmentItemArt.ReadListHandles(art, list);
            var sources = new InvestmentSources();
            byte[] bankTable = planned.Read(sources.StringBanks);
            var bankRows = InvestmentItemClone.ReadBankRows(bankTable);
            // The list names either the installed bank (first fork) or a project copy of it (re-run).
            uint? existingCopy = handles.Where(handle => handle.Type == 9).Select(handle => handle.Handle)
                .FirstOrDefault(handle => project.AddedAssets.Any(asset => asset.Native is { } native
                    && ProjectTags.Tag(native.PackageId, native.EntryIndex ?? 0) == handle && asset.BaseTagHash == bank)) is uint copy and not 0
                ? copy : null;
            if (existingCopy is null && !handles.Any(handle => handle.Type == 9 && handle.Handle == bank))
                throw new InvalidDataException($"List {list}'s text fields do not name bank 0x{bank:X8} ("
                    + string.Join(", ", handles.Where(h => h.Type == 9).Select(h => $"0x{h.Handle:X8}")) + ").");
            if (!planned.TryGetEntry(bank, out var containerEntry) || containerEntry.Reference != StringBankWriter.ContainerClass)
                throw new InvalidDataException($"0x{bank:X8} is not a string bank (class 0x80809A88).");
            byte[] baseContainer = planned.ReadOriginal(bank);
            uint baseData = BinaryPrimitives.ReadUInt32LittleEndian(baseContainer.AsSpan(0x18));
            if (!planned.TryGetEntry(baseData, out var dataEntry)) throw new InvalidDataException("The bank's English data is missing.");
            var template = StringBankTemplate.Read(bank, baseContainer, baseData, planned.ReadOriginal(baseData),
                tag => planned.TryGetEntry(tag, out var entry) && entry.Reference == StringBankWriter.DataClass);
            var merged = StringBankWriter.Read(baseContainer, planned.ReadOriginal(baseData)).ToList();
            int replaced = 0, added = 0;
            foreach (var (hash, text) in texts)
            {
                int at = merged.FindIndex(row => row.Hash == hash);
                if (at >= 0) { merged[at] = new StringBankText(hash, text); replaced++; }
                else { merged.Add(new StringBankText(hash, text)); added++; }
            }
            StringBankWriter.Validate(template, merged);
            uint containerTag, dataTag;
            int bankIndex;
            if (existingCopy is uint current)
            {
                containerTag = current;
                dataTag = BinaryPrimitives.ReadUInt32LittleEndian(planned.Read(current).AsSpan(0x18));
                var (container, data) = StringBankWriter.Write(template, dataTag, merged);
                plan.Edit(new Dictionary<uint, byte[]> { [containerTag] = container, [dataTag] = data });
                bankIndex = bankRows.FirstOrDefault(row => row.ContainerTag == containerTag)?.Index
                    ?? throw new InvalidDataException("The list's bank copy has no bank index row.");
            }
            else
            {
                int entry = plan.NextEntry;
                if (entry + 1 >= 8192) throw new InvalidDataException("The package has no room for a string bank.");
                containerTag = ProjectTags.Tag(packageId, entry);
                dataTag = ProjectTags.Tag(packageId, entry + 1);
                var (container, data) = StringBankWriter.Write(template, dataTag, merged);
                string stem = $"list{list}_bank_{bank:x8}";
                plan.Add([
                    new ProjectAssetAddition($"{stem}.strings", container,
                        new(packageId, StringBankWriter.ContainerClass, containerEntry.TypeInfo, stem, entry), bank),
                    new ProjectAssetAddition($"{stem}_en.strings", data,
                        new(packageId, StringBankWriter.DataClass, dataEntry.TypeInfo, stem + "_en", entry + 1), baseData)
                ]);
                uint nameHash = InvestmentItemClone.NewBankNameHash($"sunrise_list{list}_{bank:x8}_{packageId:x4}", bankRows);
                (bankTable, bankIndex) = InvestmentItemClone.AppendBankRow(bankTable, nameHash, containerTag);
                plan.Edit(new Dictionary<uint, byte[]> { [sources.StringBanks] = bankTable });
            }
            var map = new Dictionary<uint, uint>();
            if (existingCopy is null) map[bank] = containerTag;
            foreach (var (from, to) in icons)
            {
                if (!planned.TryGetEntry(to, out var iconEntry) || iconEntry.Reference != 0x80804A53)
                    throw new InvalidDataException($"0x{to:X8} is not an icon container (class 0x80804A53).");
                map[from] = to;
            }
            if (map.Count > 0)
            {
                byte[] forked = InvestmentItemArt.ForkListFields(art, installedLists, list, map);
                plan.Edit(new Dictionary<uint, byte[]> { [artTag] = InvestmentItemArt.Rebuild(planned.ReadOriginal(artTag), forked) });
            }
            string summary = $"List {list}: bank 0x{bank:X8} -> {(existingCopy is null ? "new" : "rewritten")} copy 0x{containerTag:X8} "
                + $"(data 0x{dataTag:X8}, bank index {bankIndex}); {replaced} string(s) replaced, {added} added"
                + (icons.Count > 0 ? $"; {icons.Count} icon handle(s) swapped" : "")
                + (map.Count > 0 ? "; list fields forked" : "");
            return new PreparedSubclassArt(key, summary, plan.Assets.ToArray(), plan.Changes.ToDictionary());
        }, plan => plan.Assets, plan => plan.Changes, plan =>
        {
            LastSubclassArt = plan;
            return plan.Summary;
        });
    }
}
