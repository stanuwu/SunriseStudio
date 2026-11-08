using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using PackageManifest = SunriseStudio.Packages.PackageManifest;

namespace SunriseStudio.Projects;

public static partial class ProjectBuilder
{
    private const uint SocketDependencyManifest = 0x81A2926F;
    private const uint SocketItemIndex = 0x81327CCB, SocketDisplayIndex = 0x81613CF1;
    private const uint SocketPlugSets = 0x81319349, SocketItemClass = ItemDefinition.Class;

    /** The Collections constructor reads plug definitions before its later plug-load pass. */
    private static Dictionary<uint, uint> PlanSocketDependencies(PackageCatalog catalog,
        IReadOnlyDictionary<uint, byte[]> edited, IReadOnlyList<AddedEntry> added)
    {
        var authored = added.ToDictionary(asset => asset.Tag);
        var items = added.Where(asset => asset.Class == SocketItemClass).Select(asset => (asset.Tag, asset.Data))
            .Concat(edited.Where(pair => catalog.TryGetEntry(pair.Key, out var entry) && entry.Reference == SocketItemClass)
                .Select(pair => (Tag: pair.Key, Data: pair.Value))).ToArray();
        if (items.Length == 0 || !added.Any(asset => asset.Class == SocketItemClass)) return [];
        byte[] Read(uint tag) => edited.TryGetValue(tag, out var current) ? current : catalog.Read(tag);
        byte[] definitions = Read(SocketItemIndex), displays = Read(SocketDisplayIndex), sets = Read(SocketPlugSets);
        int[] definitionRows = SocketRows<ItemIndexRow>(definitions, TagView.FieldOffset<ItemIndexTable>("Table"), ItemIndexRow.Class);
        int[] displayRows = SocketRows<ItemStringsIndexRow>(displays, TagView.FieldOffset<C80805cdb>("Table"), ItemStringsIndexRow.Class);
        int[] sharedRows = SocketRows<SPlugSetRow>(sets, TagView.FieldOffset<C80802cb6>("Table"), SPlugSetRow.Class);
        if (definitionRows.Length != displayRows.Length)
            throw new InvalidDataException("Socket dependency item tables do not match.");
        var result = new Dictionary<uint, uint>();
        const string ShortestItemEnd = "InventoryItemBlock.Instanced";
        var pending = new Queue<(uint Tag, byte[] Data)>(items.Where(item =>
            !TagView.FitsThrough<ItemDefinition>(item.Data, 0, ShortestItemEnd)
            || SocketField<ItemDefinition, byte>(item.Data, 0, "InventoryItemBlock.InventoryBucket") <= 2));
        var visited = new HashSet<uint>();
        while (pending.TryDequeue(out var current))
        {
            var (owner, data) = current;
            if (!visited.Add(owner)) continue;
            if (!TagView.FitsThrough<ItemDefinition>(data, 0, ShortestItemEnd)
                || SocketField<ItemDefinition, long>(data, 0, "TagSize") != data.Length)
                throw new InvalidDataException("A socket dependency item is truncated.");
            ref readonly var socketBlock = ref TagView.At<RelativePointer<SItemSocketBlock>>(data,
                TagView.FieldOffset<ItemDefinition>("SocketsItemBlock"));
            if (TagView.TargetOffset(data, in socketBlock) is not { } block) continue;
            if (block < 4 || !TagView.Fits<SItemSocketBlock>(data, block)
                || TagView.TargetClass(data, in socketBlock) != SItemSocketBlock.Class)
                throw new InvalidDataException("A socket dependency block has an unsupported type.");
            int[] sockets = SocketRows<ItemOrdinarySocketEntry>(data, block + TagView.FieldOffset<SItemSocketBlock>("Sockets"),
                ItemOrdinarySocketEntry.Class);
            if (sockets.Length > 12) throw new InvalidDataException("A socket dependency item exceeds twelve lanes.");
            foreach (int socket in sockets)
            {
                ref readonly var entry = ref TagView.At<ItemOrdinarySocketEntry>(data, socket);
                Add(entry.InitialPlugItem, owner);
                Members(data, TagView.OffsetOf(data, in entry.EmbeddedPlugs), owner);
                foreach (ushort shared in new[] { entry.ReusablePlugSetRow, entry.RandomizedPlugSetRow })
                {
                    if (shared == ushort.MaxValue) continue;
                    if (shared >= sharedRows.Length)
                        throw new InvalidDataException("A socket dependency plug set is outside its table.");
                    Members(sets, sharedRows[shared] + TagView.FieldOffset<SPlugSetRow>("Entries"), owner);
                }
            }
        }
        if (result.Count != 0 && (!catalog.TryGetEntry(SocketDependencyManifest, out var manifest)
            || manifest.Reference != PackageManifest.ClassId))
            throw new InvalidDataException("The investment globals manifest is missing.");
        return result;

        void Members(byte[] bytes, int field, uint owner)
        {
            foreach (int row in SocketRows<PlugSetMember>(bytes, field, PlugSetMember.Class))
                Add(TagView.At<PlugSetMember>(bytes, row).PlugItemIndex, owner);
        }
        void Add(uint index, uint owner)
        {
            if (index is ushort.MaxValue or uint.MaxValue) return;
            if (index >= 0x8000 || index >= definitionRows.Length)
                throw new InvalidDataException("A socket dependency names an unreachable item index.");
            ref readonly var definition = ref TagView.At<ItemIndexRow>(definitions, definitionRows[index]);
            ref readonly var display = ref TagView.At<ItemStringsIndexRow>(displays, displayRows[index]);
            uint tag = definition.ItemDefinition;
            if (!authored.TryGetValue(tag, out var item)) return;
            uint displayTag = display.ItemDisplay;
            if (item.Class != SocketItemClass || definition.ItemHash != display.ItemHash
                || !authored.TryGetValue(displayTag, out var label) || label.Class != ItemDisplayStrings.Class)
                throw new InvalidDataException("An authored socket plug has no matching definition and display.");
            if (result.TryAdd(tag, owner)) pending.Enqueue((tag, item.Data));
            result.TryAdd(displayTag, owner);
        }
    }

    /** Dependency indexes are read only from bounded arrays with the native element class. */
    private static int[] SocketRows<T>(byte[] data, int field, uint type) where T : unmanaged
    {
        int stride = Unsafe.SizeOf<T>();
        if (field < 0 || field > data.Length - Unsafe.SizeOf<TagArray<T>>())
            throw new InvalidDataException("A socket dependency array is truncated.");
        var array = new TagArray(data, field);
        long count = array.Count;
        if (count == 0) return [];
        var header = array.Header;
        if (count < 0 || count > 32768 || header.Offset < 4 || header.Offset > data.Length - TagArray.HeaderSize
            || count > (data.Length - header.DataOffset) / stride
            || !TagArray.MarkerAt(data, header.Offset)
            || header.Count != count || header.ElementClass != type)
            throw new InvalidDataException("A socket dependency array has an invalid native layout.");
        int first = checked((int)header.DataOffset);
        return Enumerable.Range(0, (int)count).Select(index => checked(first + index * stride)).ToArray();
    }

    /** @return The `member` of a TOwner at `owner`, read as T; the caller checks that it fits. */
    private static T SocketField<TOwner, T>(byte[] data, int owner, string member) where TOwner : unmanaged where T : unmanaged =>
        TagView.At<T>(data, owner + TagView.FieldOffset<TOwner>(member));
}
