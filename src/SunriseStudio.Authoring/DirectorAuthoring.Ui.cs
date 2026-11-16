using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;
using SunriseStudio.World;

namespace SunriseStudio.Authoring;

public static partial class DirectorAuthoring
{
    /** The Director's top page and EDZ's tile and map page. */
    public const uint SolarResource = 0x80BC691A, SolarHierarchy = 0x80BC691B;
    public const uint TileTemplate = 0x80BC690E, MapHierarchyTemplate = 0x80BC6966, MapResourceTemplate = 0x80BC6965;
    /** EDZ's planet texture, 648 x 324 equirectangular. */
    public const uint PlanetBitmapTemplate = 0x80D5E1F9;
    /** The largest layer of EDZ's map page. */
    public const uint MapBitmapTemplate = 0x80B46B22;
    private const uint EdzGraphHash = 0x06882FD0;
    /** EDZ's tile on the top page, as node ids. */
    private const ushort SlotFirst = 3162, SlotLast = 3291;
    /** Tile-local nodes: 71 holds the planet's screen position; 72 is the template's backdrop, hidden in EDZ's tile. */
    private const ulong TilePositionNode = 71, TileSizeNode = 72;
    private const ulong PositionX = 0x206, PositionY = 0x207, Width = 0x208, Height = 0x209;
    /** Base widget selectors: visibility (bool), opacity, tint and a text widget's string hash. */
    private const ulong Visible = 0x201, Opacity = 0x205, Tint = 0x20C, TextHash = 0x405;
    /** Layout path heads: 0x210 / 3 is the minimum size, 0x210 / 4 the maximum. */
    private const uint Constraints = 0x210, MinimumSize = 3, MaximumSize = 4;
    private const uint TextType = 0x0CE30D0B;

    /** EDZ's planet: the sphere (118), its clouds (122) and the rings around it, as tile-local nodes. */
    private const ulong PlanetSprite = 118, PlanetMaterial = 119, CloudSprite = 122, CaptionNode = 107;
    private static readonly ulong[] PlanetNodes = [117, 118, 121, 122, 125, 126, 127, 128, 129];
    /** Imported nodes EDZ placed around its planet: icons, highlight, side panel and caption. */
    private static readonly ulong[] PlanetNeighbours = [73, 96, 102, 107, 108, 110];
    /** The planet material's colour vector and its four parts. */
    private const ulong MaterialColour = 0x210, MaterialRed = 0x211, MaterialGreen = 0x212, MaterialBlue = 0x213, MaterialWeight = 0x214;

    /** EDZ's map page: its place names, its overlay art and cloud layers, as page node ids. */
    private static readonly ulong[] MapLabels = [2169, 2170, 2171, 2172, 2173, 2174, 2175, 2177, 2178, 2179, 2180, 2181, 2182, 2183,
        2184, 2185, 2186, 2187, 2188];
    private static readonly ulong[] MapOverlays = [2163, 2165, 2166, 2190, 2191, 2192, 2193, 2194, 2196, 2197];
    /** The page title text and the backdrop EDZ tints blue; both come from imported resources. */
    private const ulong MapTitle = 1945, MapBackdrop = 1880;
    /** The shell's own backdrop tint, as on the backdrop's other halves. */
    private static readonly Vector4 NeutralBackdrop = new(0.25882354f, 0.25882354f, 0.30588236f, 1);

    private static uint RootClass(PackageCatalog catalog, uint tag) =>
        catalog.TryGetEntry(tag, out var entry) ? entry!.Reference : throw new InvalidDataException($"Tag 0x{tag:X8} is missing.");

    /** A new tag copied from an installed one, keeping its class. */
    private static ProjectAssetAddition Copy(PackageCatalog catalog, uint template, string name, ushort packageId, int entry, byte[] data)
    {
        if (!catalog.TryGetEntry(template, out var source)) throw new InvalidDataException($"Template tag 0x{template:X8} is missing.");
        return new(name, data, new(packageId, source!.Reference, source.TypeInfo, null, entry), template);
    }

    /** A UI bitmap copied from `template` with one new RGBA8 texture; claims three entries. */
    private static uint Bitmap(PackageCatalog catalog, List<ProjectAssetAddition> assets, RgbaImage image, string name, ushort packageId,
        int first, uint template)
    {
        var groups = CuiBitmap.ReadGroups(catalog.Read(template));
        if (groups.Count != 1 || groups[0].Count != 1) throw new NotSupportedException("The template bitmap needs one static frame.");
        var texture = TextureAssetImporter.PrepareRgba(catalog, image, name, packageId, first, groups[0][0]);
        assets.AddRange(texture.Assets);
        assets.Add(Copy(catalog, template, name + ".bitmap", packageId, first + 2,
            InvestmentIconEdits.SetGroupTextures(catalog.Read(template), [texture.HeaderTag])));
        return TagHashes.Make(packageId, first + 2);
    }

    /** How much the planet group scales so the sphere is `size` high, in 1080-pixel units. */
    private static float PlanetScale(byte[] tile, float size)
    {
        if (!float.IsFinite(size) || size <= 0) throw new InvalidDataException("The planet needs a positive size.");
        var document = CuiDocument.Read(TileTemplate, CuiResource.Class, tile);
        var stored = Component(document, PlanetSprite)?.Properties.LastOrDefault(row => row.Direct && row.Selector == Width
            && row.PoolDescriptor == CuiDocument.FloatsB) ?? throw new InvalidDataException("EDZ's planet sphere has no stored width.");
        return size / BinaryPrimitives.ReadSingleLittleEndian(tile.AsSpan(stored.ValueOffset));
    }

    /**
     * EDZ's tile with the new row hash, planet texture, caption and size, and without Earth's clouds.
     * @param tileBase The module the tile imports; see Recentre.
     * @param scale The planet group's scale, from PlanetScale.
     */
    private static byte[] Tile(byte[] tile, byte[] tileBase, uint rowHash, uint planet, DirectorRequest request, float scale)
    {
        var document = CuiDocument.Read(TileTemplate, CuiResource.Class, tile);
        byte[] output = (byte[])tile.Clone();
        int keys = Replace(output, document, EdzTileHash, rowHash), textures = Replace(output, document, PlanetBitmapTemplate, planet);
        if (keys == 0 || textures == 0) throw new InvalidDataException("EDZ's tile no longer holds its row hash and planet texture.");
        output = Set(output, TileTemplate, TilePositionNode, [(PositionX, Snap(request.TilePosition.X)),
            (PositionY, Snap(request.TilePosition.Y))]);
        output = Rewrite(output, TileTemplate, [.. PlanetNodes, .. PlanetNeighbours], (_, value) => Snap(value * scale));
        float size = Snap(request.PlanetSize);
        // EDZ holds node 72 at 960 x 800 through its size limits; they follow the planet too.
        output = Rewrite(output, TileTemplate, [TileSizeNode], (property, _) => property.Path is [Constraints, MinimumSize or MaximumSize, _]
            ? size : null);
        output = Set(output, TileTemplate, TileSizeNode, [(Width, size), (Height, size)]);
        output = Hide(output, TileTemplate, CloudSprite);
        output = Put(output, TileTemplate, PlanetMaterial, [Vector(MaterialColour, new(1, 1, 1, 0)), FloatA(MaterialRed, 1),
            FloatA(MaterialGreen, 1), FloatA(MaterialBlue, 1), FloatA(MaterialWeight, 0)]);
        output = Put(output, TileTemplate, CaptionNode, [Hash(TextHash, request.Name.Hash)]);
        foreach (ulong node in HoverBoxNodes) output = Recentre(output, tile, tileBase, node, scale);
        return output;
    }

    /** The module every destination tile imports; it holds the hover box. */
    public const uint TileBase = 0x80BC68D4;
    /** Tile-local nodes: the hover box, and the ring and icon every stock tile centres in it. */
    private const ulong HoverBox = 95;
    private static readonly ulong[] HoverBoxNodes = [96, 102];

    /**
     * Places a node of the hover box after the planet scaled. The box keeps its size and its top-left
     * corner, so the node's offset from the box centre scales, not its position.
     * @param tile EDZ's tile before any edit.
     * @param tileBase The base module, for the lengths a tile does not store.
     */
    private static byte[] Recentre(byte[] output, byte[] tile, byte[] tileBase, ulong node, float scale)
    {
        static float? Length(byte[] bytes, uint tag, ulong node, ulong selector) =>
            Component(CuiDocument.Read(tag, CuiResource.Class, bytes), node)?.Properties
                .LastOrDefault(row => row.Direct && row.Selector == selector) is { } stored
                ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(stored.ValueOffset)) : null;
        float Stock(ulong selector) => Length(tile, TileTemplate, node, selector) ?? Length(tileBase, TileBase, node, selector) ?? 0;
        float Placed(ulong position, ulong size)
        {
            float centre = (Length(tileBase, TileBase, HoverBox, size)
                ?? throw new InvalidDataException("The tile's hover box has no size.")) / 2;
            float now = Length(output, TileTemplate, node, size) ?? Length(tileBase, TileBase, node, size) ?? 0;
            return Snap(centre + (Stock(position) + Stock(size) / 2 - centre) * scale - now / 2);
        }
        return Set(output, TileTemplate, node, [(PositionX, Placed(PositionX, Width)), (PositionY, Placed(PositionY, Height))]);
    }
    /**
     * A 1080-unit length on a whole pixel. Stock tiles sit on whole or half pixels; parts at other
     * fractions round to different pixels as the layer moves, so they shake against each other.
     */
    private static float Snap(float length) => MathF.Round(length * CuiLayout.ReferenceHeight) / CuiLayout.ReferenceHeight;

    /** Sets float layout properties on one component; a stored value keeps its pool. */
    private static byte[] Set(byte[] resource, uint tag, ulong node, IReadOnlyList<(ulong Selector, float Value)> values)
    {
        var component = Component(CuiDocument.Read(tag, CuiResource.Class, resource), node);
        return Put(resource, tag, node, values.Select(value => new CuiPropertyOverride(value.Selector,
            component?.Properties.LastOrDefault(row => row.Direct && row.Selector == value.Selector)?.PoolDescriptor ?? CuiDocument.FloatsB,
            BitConverter.GetBytes(value.Value))).ToArray());
    }

    /** Scales a component's stored 1080-unit lengths; a component the page does not override is left alone. */
    private static byte[] ScaleDirect(byte[] resource, uint tag, ulong node, float scale)
    {
        var component = Component(CuiDocument.Read(tag, CuiResource.Class, resource), node);
        var lengths = component?.Properties.Where(row => row.ValueClass == SCuiFloatValue.Class && row.PoolDescriptor == CuiDocument.FloatsB)
            .ToArray() ?? [];
        if (lengths.Any(row => !row.Direct)) throw new InvalidDataException($"Node {node} holds a planet length under a path.");
        if (lengths.Length == 0) return resource;
        return Set(resource, tag, node, lengths.Select(row => (row.Selector,
            Snap(BinaryPrimitives.ReadSingleLittleEndian(resource.AsSpan(row.ValueOffset)) * scale))).ToArray());
    }

    /** Writes direct properties on one component of the default profile. */
    private static byte[] Put(byte[] resource, uint tag, ulong node, IReadOnlyList<CuiPropertyOverride> values)
    {
        var document = CuiDocument.Read(tag, CuiResource.Class, resource);
        var component = Component(document, node) ?? throw new InvalidDataException($"Component {node} is missing.");
        return CuiPropertyEdits.Upsert(resource, document, component, values);
    }

    private static CuiComponent? Component(CuiDocument document, ulong node) =>
        document.Objects.Where(owner => owner.IsDefaultProfile).SelectMany(owner => owner.Components).LastOrDefault(row => row.Key == node);

    private static CuiPropertyOverride FloatA(ulong selector, float value) => new(selector, CuiDocument.FloatsA, BitConverter.GetBytes(value));
    private static CuiPropertyOverride Hash(ulong selector, uint value) => new(selector, CuiDocument.StringHashes, BitConverter.GetBytes(value));
    private static CuiPropertyOverride Vector(ulong selector, Vector4 value) =>
        new(selector, CuiDocument.Vectors, BitConverter.GetBytes(value.X).Concat(BitConverter.GetBytes(value.Y))
            .Concat(BitConverter.GetBytes(value.Z)).Concat(BitConverter.GetBytes(value.W)).ToArray());

    /**
     * Hides one widget: not visible, no opacity, no size, and no text on a text widget.
     * A visibility stored outside the bool pool is a condition, so it stays and the rest still hides the widget.
     */
    private static byte[] Hide(byte[] resource, uint tag, ulong node)
    {
        var document = CuiDocument.Read(tag, CuiResource.Class, resource);
        var component = Component(document, node) ?? throw new InvalidDataException($"Component {node} is missing.");
        CuiProperty? Stored(ulong selector) => component.Properties.LastOrDefault(row => row.Direct && row.Selector == selector);
        List<CuiPropertyOverride> values = [FloatA(Opacity, 0)];
        if (Stored(Opacity)?.PoolDescriptor is { } opacity && opacity != CuiDocument.FloatsA)
            throw new InvalidDataException($"Node {node} stores its opacity outside the float pool.");
        var visible = Stored(Visible);
        if (visible is null || visible.PoolDescriptor == CuiDocument.Bools)
            values.Add(new(Visible, CuiDocument.Bools, new byte[Unsafe.SizeOf<SCuiBoolValue>()]));
        foreach (ulong selector in (ulong[])[Width, Height])
            values.Add(new(selector, Stored(selector)?.PoolDescriptor ?? CuiDocument.FloatsB, BitConverter.GetBytes(0f)));
        if (document.Nodes.Any(row => row.Id == node && row.TypeHash == TextType && row.AuxiliaryTypeHash == 0))
            values.Add(Hash(TextHash, StringHash.Empty));
        return CuiPropertyEdits.Upsert(resource, document, component, values);
    }

    /**
     * Rewrites every stored 1080-unit length on the given nodes; `map` returns null to keep a value.
     * A length record shared with any other node is refused, so the edit cannot leak.
     */
    private static byte[] Rewrite(byte[] resource, uint tag, IReadOnlyCollection<ulong> nodes, Func<CuiProperty, float, float?> map)
    {
        var document = CuiDocument.Read(tag, CuiResource.Class, resource);
        var targets = document.Objects.Where(owner => owner.IsDefaultProfile).SelectMany(owner => owner.Components)
            .Where(row => nodes.Contains(row.Key)).ToHashSet(ReferenceEqualityComparer.Instance);
        var lengths = targets.Cast<CuiComponent>().SelectMany(row => row.Properties)
            .Where(row => row.ValueClass == SCuiFloatValue.Class && row.PoolDescriptor == CuiDocument.FloatsB).DistinctBy(row => row.RecordOffset)
            .ToArray();
        var records = lengths.Select(row => row.RecordOffset).ToHashSet();
        if (document.Components.Any(row => !targets.Contains(row) && row.Properties.Any(property => records.Contains(property.RecordOffset))))
            throw new InvalidDataException("A planet length is shared with a node outside the planet.");
        Dictionary<CuiProperty, ReadOnlyMemory<byte>> changes = [];
        foreach (var property in lengths)
            if (map(property, BinaryPrimitives.ReadSingleLittleEndian(resource.AsSpan(property.ValueOffset))) is { } value)
                changes[property] = BitConverter.GetBytes(value);
        return changes.Count == 0 ? resource : CuiPropertyEdits.Apply(resource, document, changes);
    }

    /** EDZ's map page hierarchy over the new resource. */
    private static byte[] PageHierarchy(byte[] hierarchy, uint resource)
    {
        byte[] output = (byte[])hierarchy.Clone();
        TagView.Edit<SCuiHierarchy>(output).CuiResource = resource;
        return output;
    }

    /** EDZ's map page naming the new graph, with the new map image and none of EDZ's names or overlay art. */
    private static byte[] PageResource(byte[] page, uint graphHash, uint map, TextReference name)
    {
        var document = CuiDocument.Read(MapResourceTemplate, CuiResource.Class, page);
        byte[] output = (byte[])page.Clone();
        if (Replace(output, document, EdzGraphHash, graphHash) == 0 || Replace(output, document, MapBitmapTemplate, map) == 0)
            throw new InvalidDataException("EDZ's map page no longer names its graph and map image.");
        foreach (ulong node in MapLabels.Concat(MapOverlays)) output = Hide(output, MapResourceTemplate, node);
        output = Put(output, MapResourceTemplate, MapTitle, [Hash(TextHash, name.Hash)]);
        return Put(output, MapResourceTemplate, MapBackdrop, [Vector(Tint, NeutralBackdrop)]);
    }

    /** Writes `to` over every u32 property value equal to `from`; returns how many pool slots changed. */
    private static int Replace(byte[] bytes, CuiDocument document, uint from, uint to)
    {
        var slots = document.Properties.Where(property => property.Fields.Count == 1 && property.ValueOffset >= 0
            && property.ValueOffset + 4 <= bytes.Length && U32(bytes, property.ValueOffset) == from).Select(property => property.ValueOffset)
            .Distinct().ToArray();
        foreach (int slot in slots) W32(bytes, slot, to);
        return slots.Length;
    }

    /** EDZ's graph cut to its patrol node, with the new art key, name and map transform. */
    private static byte[] DisplayGraph(byte[] graph, RegistryIndex schema, DirectorRequest request, uint artKey, uint nodeHash, uint transform)
    {
        int nodes = TagView.FieldOffset<C80805e79>(nameof(C80805e79.Nodes));
        byte[] output = TagRelocator.RewriteArray(Parse(graph, GraphClass, schema), schema, nodes, [TemplateNode]);
        W32(output, TagView.FieldOffset<C80805e79>(nameof(C80805e79.ArtKey)), artKey);
        Text(output, 12, request.Name);
        Text(output, 20, request.ShortName);
        W32(output, TagView.FieldOffset<C80805e79>(nameof(C80805e79.MapTransform)), transform);
        int node = TagArray.Checked(output, nodes, stride: Unsafe.SizeOf<C80805e88>()).Data;
        W32(output, node + TagView.FieldOffset<C80805e88>(nameof(C80805e88.NodeHash)), nodeHash);
        // EDZ's patrol node lists four activity rows where its other nodes list one; the copy keeps one.
        int activities = node + TagView.FieldOffset<C80805e88>(nameof(C80805e88.Activities));
        if (TagArray.Checked(output, activities, stride: Unsafe.SizeOf<C80805e8c>()).Count > 1)
            output = TagRelocator.RewriteArray(Parse(output, GraphClass, schema), schema, activities, [0]);
        node = TagArray.Checked(output, nodes, stride: Unsafe.SizeOf<C80805e88>()).Data;
        Text(output, node + NodeName, request.Name);
        Text(output, node + NodeDescription, request.Zones[0].Action);
        return output;
    }

    /** A display node's own name and description, as bank and hash pairs; stock nodes leave them 0xFFFF and empty. */
    private const int NodeName = 60, NodeDescription = 68;

    /** EDZ's definition graph cut to its patrol node, which lists only the destination's activity. */
    private static byte[] DefinitionGraph(byte[] graph, RegistryIndex schema, uint nodeHash, int activity)
    {
        int nodes = TagView.FieldOffset<C80807512>(nameof(C80807512.Nodes));
        byte[] output = TagRelocator.RewriteArray(Parse(graph, DefinitionGraphClass, schema), schema, nodes, [TemplateNode]);
        int node = TagArray.Checked(output, nodes, stride: Unsafe.SizeOf<C80807514>()).Data;
        W32(output, node, nodeHash);
        int activities = node + TagView.FieldOffset<C80807514>(nameof(C80807514.Activities));
        output = TagRelocator.RewriteArray(Parse(output, DefinitionGraphClass, schema), schema, activities, [0]);
        int entry = TagArray.Checked(output, activities, stride: Unsafe.SizeOf<C80807519>()).Data;
        W32(output, entry, nodeHash);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(entry + 4), activity);
        return output;
    }

    /** Map page nodes: the transform spans container 2158; the map image 2159 sits inside it. */
    private const ulong MapContainer = 2158, MapImage = 2159;

    /**
     * EDZ's map transform moved so the map image covers the destination's map rectangle. The transform
     * spans the image's container, so its corner and size reach past the image by the image's offset.
     */
    private static byte[] Transform(byte[] transform, byte[] page, Vector2 origin, Vector2 size)
    {
        if (size.X <= 0 || size.Y <= 0) throw new InvalidDataException("The map must cover a positive area.");
        var document = CuiDocument.Read(MapResourceTemplate, CuiResource.Class, page);
        float Pixels(ulong node, ulong selector) => Component(document, node)?.Properties
            .LastOrDefault(row => row.Direct && row.Selector == selector && row.PoolDescriptor == CuiDocument.FloatsB) is { } stored
            ? BinaryPrimitives.ReadSingleLittleEndian(page.AsSpan(stored.ValueOffset)) * CuiLayout.ReferenceHeight
            : throw new InvalidDataException($"EDZ's map node {node} has no stored {selector:X}.");
        // World units per page pixel; world y runs up and page y down.
        var scale = new Vector2(size.X / Pixels(MapImage, Width), size.Y / Pixels(MapImage, Height));
        byte[] output = (byte[])transform.Clone();
        WF(output, 16, origin.X - Pixels(MapImage, PositionX) * scale.X);
        WF(output, 20, origin.Y + Pixels(MapImage, PositionY) * scale.Y);
        WF(output, 64, Pixels(MapContainer, Width) * scale.X);
        WF(output, 68, Pixels(MapContainer, Height) * scale.Y);
        return output;
    }

    /**
     * A copy of EDZ's tile on the top page, in a parallax layer of its own. The client numbers nodes in
     * build order: node 0 is the screen, then each import's nodes, then the page's own. So the new layer
     * and tile imports follow the last installed one, and every page-local node id moves up past them.
     * @param depth The new layer's parallax depth, as the stock layers' 1.01 to 1.12.
     */
    private static (byte[] Resource, byte[] Hierarchy) SolarSlot(byte[] page, byte[] hierarchy, RegistryIndex schema, uint tile,
        float scale, float depth)
    {
        if (!float.IsFinite(depth) || depth <= 0) throw new InvalidDataException("The planet's parallax depth must be positive.");
        var program = CuiDocument.Read(SolarHierarchy, SCuiHierarchy.Class, hierarchy);
        var resource = CuiDocument.Read(SolarResource, CuiResource.Class, page);
        int imported = resource.Imports.Sum(row => row.NodeCount), firstLocal = imported + 1;
        if (program.Hierarchy.Count != firstLocal + resource.Nodes.Count)
            throw new InvalidDataException("The top page's node count differs from its imports and own nodes.");
        // An import at FirstNode F holds nodes F + 1 to F + count.
        var tiles = resource.Imports.Where(row => row.FirstNode + 1 >= SlotFirst && row.FirstNode + row.NodeCount <= SlotLast).ToArray();
        if (tiles is not [{ TagHash: TileTemplate } edzTile] || edzTile.NodeCount != SlotLast - SlotFirst + 1)
            throw new InvalidDataException("EDZ's tile on the top page has changed.");
        var parents = program.Hierarchy.ToDictionary(edge => (int)edge.Id, edge => (int)edge.Parent);
        int edzContent = parents[edzTile.FirstNode + TileRoot];
        var edzLayer = resource.Imports.SingleOrDefault(row => row.TagHash == ParallaxLayer && row.FirstNode + LayerContent == edzContent)
            ?? throw new InvalidDataException("EDZ's tile no longer hangs in a parallax layer.");
        // The panel drives the layers it lists, each by an anchor node inside the layer's content.
        var anchors = LayerAnchors(resource, page);
        int edzAnchorId = anchors.Select(row => row.Node).SingleOrDefault(node => parents.GetValueOrDefault(node) == edzContent);
        var edzAnchor = resource.Imports.SingleOrDefault(row => row.TagHash == LayerAnchor && row.FirstNode + 1 == edzAnchorId && row.NodeCount == 1)
            ?? throw new InvalidDataException("EDZ's parallax layer has no anchor in the panel's layer list.");
        CuiImport[] imports = [edzLayer, edzAnchor, edzTile];
        int delta = imports.Sum(row => row.NodeCount);
        if (program.Hierarchy.Count + delta > CuiNodeLimit) throw new InvalidDataException("The top page has no node ids left.");
        int Local(int id) => id >= firstLocal && id != NoParent ? id + delta : id;
        // The copies take the new imports' ids in import order: the layer, its anchor, then the tile.
        var starts = new Dictionary<CuiImport, int>();
        int cursor = imported;
        foreach (var row in imports) { starts[row] = cursor; cursor += row.NodeCount; }
        CuiImport? Owner(int id) => imports.FirstOrDefault(row => id > row.FirstNode && id <= row.FirstNode + row.NodeCount);
        bool Copied(int id) => Owner(id) is not null;
        int Copy(int id) => Owner(id) is { } row ? id - row.FirstNode + starts[row] : throw new InvalidDataException($"Node {id} is not copied.");

        byte[] output = ShiftLocals(page, resource, firstLocal, delta);
        int importField = TagView.FieldOffset<CuiResource>(nameof(CuiResource.TemplateReferences)), importStride = Unsafe.SizeOf<SCuiImport>();
        var rows = imports.Select(row =>
        {
            byte[] bytes = page.AsSpan(row.Offset, importStride).ToArray();
            ref var copy = ref TagView.Edit<SCuiImport>(bytes);
            if (row.TagHash == TileTemplate) copy.Resource = tile;
            copy.FirstNode = copy.MirrorFirst = checked((ushort)starts[row]);
            return bytes;
        }).ToArray();
        output = TagRelocator.AppendRows(Parse(output, CuiResource.Class, schema), schema, importField, rows,
            imports.Select(row => row.Offset).ToArray());

        // The page's own overrides for the layer's and the slot's nodes, rekeyed to the copies.
        var shifted = CuiDocument.Read(SolarResource, CuiResource.Class, output);
        int componentStride = Unsafe.SizeOf<CuiComponentRow>();
        foreach (var owner in shifted.Objects)
        {
            var slotRows = owner.Components.Where(component => component.Key < NoParent && Copied((int)component.Key)).ToArray();
            if (slotRows.Length == 0) continue;
            int field = owner.Offset + TagView.FieldOffset<CuiObjectRow>(nameof(CuiObjectRow.Components));
            var copies = slotRows.Select(component =>
            {
                byte[] bytes = output.AsSpan(component.Offset, componentStride).ToArray();
                TagView.Edit<CuiComponentRow>(bytes).NodeKey = (ulong)Copy((int)component.Key);
                return bytes;
            }).ToArray();
            output = TagRelocator.AppendRows(Parse(output, CuiResource.Class, schema), schema, field, copies,
                slotRows.Select(component => component.Offset).ToArray());
        }
        // The page's own sizes for the planet's nodes scale with it, as the tile's do.
        foreach (ulong local in PlanetNodes)
            output = ScaleDirect(output, SolarResource, (ulong)Copy(edzTile.FirstNode + (int)local), scale);
        // Each stock layer's depth is a page override on its node 2; the copy carries EDZ's until this.
        output = Put(output, SolarResource, (ulong)Copy(edzLayer.FirstNode + LayerDepthNode), [FloatA(LayerDepth, depth)]);
        // The panel lists its layers in depth order, as the layer roots run; the new anchor joins at its depth.
        int slot = anchors.FindIndex(row => LayerDepthOf(resource, page, parents[row.Node] - LayerContent) > depth) is var found and >= 0
            ? found : anchors.Count;
        output = RegisterLayer(output, slot, Copy(edzAnchorId));

        // The page binds its graph hash into each tile's row component; the copy needs its own binding.
        int bindingField = TagView.FieldOffset<CuiResource>("BindingSet.Bindings"), bindingStride = Unsafe.SizeOf<CuiBindingRow>();
        var bindings = TagArray.Checked(output, bindingField, CuiBindingRow.Class, bindingStride);
        ushort Rekey(ushort id) => Copied(id) ? checked((ushort)Copy(id)) : id;
        var slotBindings = Enumerable.Range(0, bindings.Count).Select(index => bindings.Data + index * bindingStride).Where(at =>
        {
            var row = TagView.At<CuiBindingRow>(output, at);
            return Copied(row.SourceNode) || Copied(row.TargetNode);
        }).ToArray();
        if (slotBindings.Length == 0) throw new InvalidDataException("EDZ's tile has no binding on the top page.");
        var bindingCopies = slotBindings.Select(at =>
        {
            byte[] bytes = output.AsSpan(at, bindingStride).ToArray();
            ref var copy = ref TagView.Edit<CuiBindingRow>(bytes);
            copy.SourceNode = Rekey(copy.SourceNode);
            copy.SourceNodeCopy = Rekey(copy.SourceNodeCopy);
            copy.TargetNode = Rekey(copy.TargetNode);
            copy.TargetNodeCopy = Rekey(copy.TargetNodeCopy);
            return bytes;
        }).ToArray();
        output = TagRelocator.AppendRows(Parse(output, CuiResource.Class, schema), schema, bindingField, bindingCopies, slotBindings);

        // Installed edges keep their order with local ids shifted; the copies' edges follow in the slots' order.
        int edgeField = TagView.FieldOffset<SCuiHierarchy>(nameof(SCuiHierarchy.Edges)), edgeStride = Unsafe.SizeOf<SCuiHierarchyEdge>();
        byte[] tree = (byte[])hierarchy.Clone();
        foreach (var edge in program.Hierarchy)
        {
            ref var row = ref TagView.Edit<SCuiHierarchyEdge>(tree, edge.Offset);
            row.Child = checked((ushort)Local(edge.Id));
            row.Parent = checked((ushort)Local(edge.Parent));
        }
        byte[] CopyEdge(CuiHierarchyNode edge, int parent)
        {
            byte[] bytes = new byte[edgeStride];
            ref var copy = ref TagView.Edit<SCuiHierarchyEdge>(bytes);
            copy.Child = checked((ushort)Copy(edge.Id));
            copy.Parent = checked((ushort)parent);
            return bytes;
        }
        // The anchor and the tile's root hang on the new layer's content node, as EDZ's hang on their layer's.
        byte[][] Edges(CuiImport import) => program.Hierarchy.Where(edge => Owner(edge.Id) == import)
            .Select(edge => CopyEdge(edge, Copied(edge.Parent) ? Copy(edge.Parent) : Local(edge.Parent))).ToArray();
        byte[][] layerEdges = [.. Edges(edzLayer), .. Edges(edzAnchor)], tileEdges = Edges(edzTile);
        tree = TagRelocator.AppendRows(Parse(tree, SCuiHierarchy.Class, schema), schema, edgeField, [.. layerEdges, .. tileEdges],
            layerEdges.Concat(tileEdges).Select(_ => -1).ToArray());

        // Siblings draw in edge order and the stock layers run by depth; the new layer goes before the first deeper one.
        var deeper = anchors.Select(row => parents[row.Node] - LayerContent).Where(first => LayerDepthOf(resource, page, first) > depth)
            .Select(first => first + 1).ToHashSet();
        int edgeData = TagArray.Checked(tree, edgeField, SCuiHierarchyEdge.Class, edgeStride).Data;
        int before = Enumerable.Range(0, program.Hierarchy.Count)
            .FirstOrDefault(index => deeper.Contains(program.Hierarchy[index].Id), program.Hierarchy.Count);
        int moved = layerEdges.Length * edgeStride, start = edgeData + before * edgeStride, end = edgeData + program.Hierarchy.Count * edgeStride;
        byte[] layerRows = tree.AsSpan(end, moved).ToArray();
        tree.AsSpan(start, end - start).CopyTo(tree.AsSpan(start + moved));
        layerRows.CopyTo(tree.AsSpan(start));

        ref var header = ref TagView.Edit<SCuiHierarchy>(tree);
        header.NodeCountA = header.NodeCountB = checked((ushort)(program.Hierarchy.Count + delta));
        return (output, tree);
    }

    /**
     * A stock layer's depth: the page's 0x206 override on the layer's node 2.
     * @param first The node before the layer's root; the first layer is nested in the shell, not a page import.
     */
    private static float LayerDepthOf(CuiDocument resource, byte[] page, int first) =>
        Component(resource, (ulong)(first + LayerDepthNode))?.Properties
            .LastOrDefault(row => row.Direct && row.Selector == LayerDepth && row.PoolDescriptor == CuiDocument.FloatsA) is { } stored
            ? BinaryPrimitives.ReadSingleLittleEndian(page.AsSpan(stored.ValueOffset))
            : throw new InvalidDataException($"The parallax layer at node {first + 1} has no depth.");

    /** The panel's layer list: (property, anchor node) for each 0x600 / (k << 16 | 6) entry, in k order. */
    private static List<(CuiProperty Property, int Node)> LayerAnchors(CuiDocument resource, byte[] page)
    {
        var panel = Component(resource, LayerPanel) ?? throw new InvalidDataException("The top page does not set the parallax panel.");
        var entries = panel.Properties.Where(row => row.Path is [LayerList, var element] && (element & 0xFFFF) == AnchorField
            && row.ValueClass == CuiShortValue.Class).OrderBy(row => row.Path[1] >> 16).ToList();
        if (entries.Select((row, index) => row.Path[1] >> 16 == index).Any(ordered => !ordered))
            throw new InvalidDataException("The parallax panel's layer list has a gap.");
        return entries.Select(row => (row, (int)BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(row.ValueOffset)))).ToList();
    }

    /**
     * Inserts an anchor into the panel's layer list at `slot` and raises the list's count. Later entries
     * move up one, and the last moves into a new entry built on the list's own path slot.
     */
    private static byte[] RegisterLayer(byte[] page, int slot, int anchor)
    {
        var document = CuiDocument.Read(SolarResource, CuiResource.Class, page);
        var panel = Component(document, LayerPanel)!;
        var entries = LayerAnchors(document, page);
        var count = panel.Properties.LastOrDefault(row => row.Path is [LayerList, LayerCount] && row.ValueClass == CuiIntegerValue.Class)
            ?? throw new InvalidDataException("The parallax panel's layer list has no count.");
        if (BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(count.ValueOffset)) != entries.Count)
            throw new InvalidDataException("The parallax panel's layer count differs from its list.");
        var values = entries.Select(row => row.Node).ToList();
        values.Insert(slot, anchor);
        var changes = new Dictionary<CuiProperty, ReadOnlyMemory<byte>> { [count] = BitConverter.GetBytes(values.Count) };
        for (int index = slot; index < entries.Count; index++) changes[entries[index].Property] = BitConverter.GetBytes(checked((ushort)values[index]));
        byte[] output = CuiPropertyEdits.Apply(page, document, changes);
        var template = LayerAnchors(CuiDocument.Read(SolarResource, CuiResource.Class, output), output)[^1].Property;
        return AppendPathProperty(output, LayerPanel, template, (ulong)entries.Count << 16 | AnchorField,
            BitConverter.GetBytes(checked((ushort)values[^1])));
    }

    /**
     * Adds a property to a component, on the template property's path slot and in its value pool.
     * Every pool user is re-pointed at the grown pool, as CuiPropertyEdits.Upsert does for direct properties.
     */
    private static byte[] AppendPathProperty(byte[] resource, ulong node, CuiProperty template, ulong selector, byte[] value)
    {
        var document = CuiDocument.Read(SolarResource, CuiResource.Class, resource);
        var component = Component(document, node) ?? throw new InvalidDataException($"Component {node} is missing.");
        if (!component.Properties.Any(row => row.RecordOffset == template.RecordOffset)) throw new InvalidDataException("The template property belongs to another component.");
        if (document.Components.Count(row => row.Offset == component.Offset || row.Properties.Any(property =>
            component.Properties.Any(own => own.RecordOffset == property.RecordOffset))) != 1)
            throw new InvalidDataException($"Component {node} shares its property rows.");
        var pool = document.Pools.Single(row => row.DescriptorOffset == template.PoolDescriptor);
        if (value.Length != pool.Stride) throw new InvalidDataException("The value does not fit the template's pool.");
        byte[] output = (byte[])resource.Clone();
        int data = TagRelocator.AppendArray(ref output, pool.DescriptorOffset, pool.ClassHash, pool.Count + 1, (pool.Count + 1) * pool.Stride, 8);
        resource.AsSpan(pool.DataOffset, pool.Count * pool.Stride).CopyTo(output.AsSpan(data));
        foreach (var property in document.Properties.Where(row => row.PoolDescriptor == pool.DescriptorOffset).DistinctBy(row => row.RecordOffset))
            TagView.SetTarget(output, in TagView.At<CuiPropertyRow>(output, property.RecordOffset).Value, data + property.ValueOffset - pool.DataOffset);
        int valueAt = data + pool.Count * pool.Stride;
        value.CopyTo(output, valueAt);

        int stride = Unsafe.SizeOf<CuiPropertyRow>();
        int field = TagView.OffsetOf(output, in TagView.At<CuiComponentRow>(output, component.Offset).Properties);
        var old = TagArray.Checked(output, field, CuiPropertyRow.Class, stride);
        int rows = TagRelocator.AppendArray(ref output, field, CuiPropertyRow.Class, old.Count + 1, (old.Count + 1) * stride, 8);
        void CopyRow(int from, int to)
        {
            output.AsSpan(from, stride).CopyTo(output.AsSpan(to));
            ref readonly var source = ref TagView.At<CuiPropertyRow>(output, from);
            int slot = TagPointer.Checked(output, TagView.OffsetOf(output, in source.PathSlot));
            int target = TagPointer.Checked(output, TagView.OffsetOf(output, in source.Value));
            TagView.SetTarget(output, in TagView.At<CuiPropertyRow>(output, to).PathSlot, slot);
            TagView.SetTarget(output, in TagView.At<CuiPropertyRow>(output, to).Value, target);
        }
        for (int index = 0; index < old.Count; index++) CopyRow(old.Data + index * stride, rows + index * stride);
        int added = rows + old.Count * stride;
        CopyRow(template.RecordOffset, added);
        TagView.Edit<CuiPropertyRow>(output, added).Selector = selector;
        TagView.SetTarget(output, in TagView.At<CuiPropertyRow>(output, added).Value, valueAt);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(TagView.FieldOffset<CuiResource>("TagSize")), output.Length);
        _ = CuiDocument.Read(SolarResource, CuiResource.Class, output);
        return output;
    }

    /** The top page's parallax panel and its layer list: 0x600 / 0 holds the count, 0x600 / (k << 16 | 6) entry k's anchor node. */
    private const ulong LayerPanel = 1882;
    private const uint LayerList = 0x600, LayerCount = 0, AnchorField = 6;
    /** The one-node module each layer's anchor is, placed in the layer's content. */
    private const uint LayerAnchor = 0x80BC6907;

    /** A planet tile's root node, as a local id of the tile. */
    private const int TileRoot = 68;
    /** The parallax layer module: node 1 its root, node 2 carries the depth in 0x206, node 7 holds the tiles. */
    private const uint ParallaxLayer = 0x80BC6829;
    private const int LayerDepthNode = 2, LayerContent = 7;
    private const ulong LayerDepth = 0x206;

    /** No parent: the hierarchy root's parent word. */
    private const int NoParent = 0x7FFF;
    /** The client builds at most this many nodes per interface. */
    private const int CuiNodeLimit = 5120;

    /**
     * Moves every page-local node id at or above `firstLocal` up by `delta`: the definitions, the
     * component keys, the bindings and the animation targets.
     */
    private static byte[] ShiftLocals(byte[] page, CuiDocument resource, int firstLocal, int delta)
    {
        byte[] output = (byte[])page.Clone();
        ushort Shift(ushort id) => id >= firstLocal && id != NoParent ? checked((ushort)(id + delta)) : id;
        var definitions = TagArray.Checked(output, TagView.FieldOffset<CuiResource>(nameof(CuiResource.Components)), CuiTypeRow.Class,
            Unsafe.SizeOf<CuiTypeRow>());
        for (int index = 0; index < definitions.Count; index++)
        {
            ref var row = ref TagView.Edit<CuiTypeRow>(output, definitions.Data + index * Unsafe.SizeOf<CuiTypeRow>());
            row.NodeId = Shift(row.NodeId);
        }
        foreach (var component in resource.Components)
        {
            ref var row = ref TagView.Edit<CuiComponentRow>(output, component.Offset);
            if (row.NodeKey >= (ulong)firstLocal && row.NodeKey < NoParent) row.NodeKey += (ulong)delta;
        }
        int bindingStride = Unsafe.SizeOf<CuiBindingRow>();
        var bindings = TagArray.Checked(output, TagView.FieldOffset<CuiResource>("BindingSet.Bindings"), CuiBindingRow.Class, bindingStride);
        for (int index = 0; index < bindings.Count; index++)
        {
            ref var row = ref TagView.Edit<CuiBindingRow>(output, bindings.Data + index * bindingStride);
            row.SourceNode = Shift(row.SourceNode);
            row.SourceNodeCopy = Shift(row.SourceNodeCopy);
            row.TargetNode = Shift(row.TargetNode);
            row.TargetNodeCopy = Shift(row.TargetNodeCopy);
        }
        int trackStride = Unsafe.SizeOf<SCuiAnimationTrack>(), targetStride = Unsafe.SizeOf<SCuiAnimationTarget>();
        foreach (var owner in resource.Objects)
        {
            var tracks = TagArray.Checked(output, owner.Offset + TagView.FieldOffset<CuiObjectRow>(nameof(CuiObjectRow.Animations)),
                SCuiAnimationTrack.Class, trackStride);
            for (int track = 0; track < tracks.Count; track++)
            {
                int at = tracks.Data + track * trackStride;
                var targets = TagArray.Checked(output, at + TagView.FieldOffset<SCuiAnimationTrack>(nameof(SCuiAnimationTrack.Components)),
                    SCuiAnimationTarget.Class, targetStride);
                for (int target = 0; target < targets.Count; target++)
                {
                    ref var row = ref TagView.Edit<SCuiAnimationTarget>(output, targets.Data + target * targetStride);
                    row.Node = Shift(row.Node);
                }
            }
        }
        return output;
    }
}
