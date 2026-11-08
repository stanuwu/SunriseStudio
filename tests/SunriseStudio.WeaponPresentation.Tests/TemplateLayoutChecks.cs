using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/** Template checks refuse a layout break and accept a byte change outside the facts the code reads. */
internal static class TemplateLayoutChecks
{
    private const ushort Package = 0xB1E;
    private delegate Span<SMaterialTextureAssignment> TextureRows(byte[] bytes);

    public static void Run(PackageCatalog catalog)
    {
        try
        {
            Presentation(catalog);
            ArtEntity(catalog);
            Particles(catalog);
            Points(catalog);
            Hud(catalog);
            Material(catalog);
        }
        finally { catalog.ReadOverride = null; catalog.SetOverlayEntries([]); }
        Console.WriteLine("template_layout presentation=pass art=pass particles=pass points=pass hud=pass material=pass");
    }

    private static uint Tag(int entry) => TagHashes.Make(Package, entry);

    /** A clip name row's duration is not layout; the clip array class is. */
    private static void Presentation(PackageCatalog catalog)
    {
        uint template = WeaponPresentationAuthoring.DefinitionTemplate;
        byte[] changed = catalog.Read(template).ToArray();
        var names = TagView.CheckedRows(changed, in TagView.Checked<SAnimationDefinition>(changed, 0).MotionDefinitions, SAnimationNameRow.Class);
        int duration = TagView.OffsetOf(changed, in names[0].DurationSeconds);
        TagView.Edit<float>(changed, duration) += 1;
        Use(catalog, template, changed);
        var prepared = WeaponPresentationAuthoring.Prepare(catalog, Package, 0, "Layout fixture", Tag(100), Tag(101));
        byte[] output = prepared.Assets.Single(asset => asset.BaseTagHash == template).Data;
        Check(TagRead.F32(output, duration) == TagRead.F32(changed, duration), "An unrelated definition change was not carried.");
        byte[] broken = catalog.ReadOriginal(template).ToArray();
        int clips = TagView.OffsetOf(broken, in TagView.Checked<SAnimationDefinition>(broken, 0).Animations);
        TagView.Edit<uint>(broken, new TagArray(broken, clips).HeaderOffset + TagArray.ElementClassOffset) = SAnimationNameRow.Class;
        Use(catalog, template, broken);
        Refused(() => WeaponPresentationAuthoring.Prepare(catalog, Package, 0, "Layout fixture", Tag(100), Tag(101)), "Unexpected array class");
    }

    /** A model component float is not layout; the component's class word is. */
    private static void ArtEntity(PackageCatalog catalog)
    {
        const uint root = 0x81A6B357, config = 0x81A6B355;
        byte[] changed = catalog.Read(config).ToArray();
        int resource = TagView.TargetOffset(changed, in TagView.Checked<SEntityConfig>(changed, 0).Definition)!.Value;
        Check(TagView.FitsThrough<SModelComponent>(changed, resource, nameof(SModelComponent.UnkA0)), "The art component lost its fixture field.");
        int field = resource + TagView.FieldOffset<SModelComponent>(nameof(SModelComponent.UnkA0));
        TagView.Edit<float>(changed, field) += 1;
        Use(catalog, config, changed);
        var prepared = WeaponAssetEntityCompiler.Prepare(catalog, root, Tag(110), Package, 10, "Layout art");
        Check(TagRead.F32(prepared.Assets[0].Data, field) == TagRead.F32(changed, field), "An unrelated art component change was not carried.");
        byte[] broken = catalog.ReadOriginal(config).ToArray();
        TagView.WriteClassWord(broken, resource, SModelComponentPrimary.Class);
        Use(catalog, config, broken);
        Refused(() => WeaponAssetEntityCompiler.Prepare(catalog, root, Tag(110), Package, 10, "Layout art"), "component layout changed");
    }

    /** An unread resource word is not layout; the work node class and the root's sequence link are. */
    private static void Particles(PackageCatalog catalog)
    {
        uint config = WeaponParticleBinding.ConfigTemplate;
        byte[] changed = catalog.Read(config).ToArray();
        int resource = WeaponParticleBinding.SequenceResource(changed);
        int field = resource + TagView.FieldOffset<SParticleSequenceResource>(nameof(SParticleSequenceResource.Unk140));
        TagView.Edit<uint>(changed, field) ^= 1;
        Check(TagRead.U32(changed, field) != config, "The fixture word became an owner reference.");
        Use(catalog, config, changed);
        WeaponParticleBurst[] burst = [new(Tag(190), .5f, 0x12345678)];
        var prepared = WeaponParticleBinding.PrepareSequence(catalog, burst, Package, 20, "Layout sequence");
        Check(TagRead.U32(prepared.Data, field) == TagRead.U32(changed, field), "An unrelated sequence change was not carried.");
        byte[] broken = catalog.ReadOriginal(config).ToArray();
        var works = TagView.CheckedRows(broken, in TagView.At<SParticleSequenceResource>(broken, resource).WorkNodes, SSequenceFlowRow.Class);
        TagView.WriteClassWord(broken, TagView.TargetOrSelf(broken, in works[0].OwnerPointer), SEffectNodeLink.Class);
        Use(catalog, config, broken);
        Refused(() => WeaponParticleBinding.PrepareSequence(catalog, burst, Package, 20, "Layout sequence"), "timed particle node");
        byte[] entity = catalog.ReadOriginal(WeaponParticleBinding.EntityTemplate).ToArray();
        foreach (ref var row in TagView.EditRows(entity, in TagView.At<SEntity>(entity).Components, SPatternComponent.Class))
            if (row.Config == config) row.Config = 0x80C5CE33;
        Use(catalog, WeaponParticleBinding.EntityTemplate, entity);
        Refused(() => WeaponParticleBinding.ProfileEntity(catalog, Tag(20)), "one matching config");
    }

    /** A stock point's translation is not layout; data after the interface table is. */
    private static void Points(PackageCatalog catalog)
    {
        const uint gameplay = WeaponPresentationAuthoring.GameplayTemplate;
        uint template = WeaponAttachmentAuthoring.TemplateTag;
        byte[] changed = catalog.Read(template).ToArray();
        int definition = TagView.TargetOffset(changed, in TagView.Checked<SEntityConfig>(changed, 0).Definition)!.Value;
        var rows = TagArray.Checked(changed, definition + TagView.FieldOffset<C80808507>("H09da30c1.H7bf0a1ea.Points"),
            SWeaponAttachmentPoint.Class, Unsafe.SizeOf<SWeaponAttachmentPoint>());
        TagView.Edit<SWeaponAttachmentPoint>(changed, rows.Data).Translation += new Vector3(.5f, 0, 0);
        Use(catalog, template, changed);
        var prepared = WeaponAttachmentAuthoring.Prepare(catalog, catalog.Read(gameplay), Package, 30, "Layout points", []);
        Check(TagView.At<SWeaponAttachmentPoint>(prepared.Asset.Data, rows.Data).Translation
            == TagView.At<SWeaponAttachmentPoint>(changed, rows.Data).Translation, "An unrelated point change was not carried.");
        byte[] original = catalog.ReadOriginal(template), grown = new byte[original.Length + TagArray.HeaderSize];
        original.CopyTo(grown, 0);
        TagView.Edit<SEntityConfig>(grown).TagByteLength = (ulong)grown.Length;
        Use(catalog, template, grown);
        Refused(() => WeaponAttachmentAuthoring.Prepare(catalog, catalog.Read(gameplay), Package, 30, "Layout points", []), "interface table");
    }

    /** An unread HUD resource word is not layout; the variant table class is. */
    private static void Hud(PackageCatalog catalog)
    {
        uint template = WeaponHudIconEdits.ConfigTemplate;
        catalog.ReadOverride = null;
        var presentation = WeaponPresentationAuthoring.Prepare(catalog, Package, 40, "Layout hud", Tag(102), Tag(103));
        var bytes = presentation.Assets.ToDictionary(asset => Tag(asset.Native!.EntryIndex!.Value), asset => asset.Data);
        catalog.SetOverlayEntries(presentation.Assets.Select(asset => new PackageOverlayEntry(new PackageEntry(
            Tag(asset.Native!.EntryIndex!.Value), asset.Native.Reference, (int)((asset.Native.TypeInfo >> 9) & 127),
            (int)((asset.Native.TypeInfo >> 6) & 7), asset.Data.Length, "w64_layout_fixture_0b1e_0.pkg", Package,
            asset.Native.EntryIndex.Value) { TypeInfo = asset.Native.TypeInfo }, asset.Name)).ToList());
        string directory = Path.Combine(Path.GetTempPath(), "sunrise-template-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = StudioProject.Create(directory, "Template layout fixture");
            var image = new RgbaImage(WeaponHudIconEdits.Width, WeaponHudIconEdits.Height,
                new byte[WeaponHudIconEdits.Width * WeaponHudIconEdits.Height * 4]);
            PreparedWeaponHudIcon Prepare(byte[] config)
            {
                catalog.ReadOverride = tag => tag == template ? config : bytes.GetValueOrDefault(tag);
                return WeaponHudIconAuthoring.Prepare(project, catalog, presentation.GameplayEntityTag, image, "Layout hud", Package,
                    50, presentation.Assets);
            }
            byte[] changed = catalog.ReadOriginal(template).ToArray();
            int resource = TagView.TargetOffset(changed, in TagView.Checked<SEntityConfig>(changed, 0).Definition)!.Value;
            int field = resource + TagView.FieldOffset<SWeaponHudConfigResource>(nameof(SWeaponHudConfigResource.Unk50));
            TagView.Edit<uint>(changed, field) ^= 1;
            Check(TagRead.U32(changed, field) != template, "The fixture word became an owner reference.");
            byte[] output = Prepare(changed).Assets.Single(asset => asset.Name.EndsWith(".hud.config", StringComparison.Ordinal)).Data;
            Check(TagRead.U32(output, field) == TagRead.U32(changed, field), "An unrelated HUD config change was not carried.");
            byte[] broken = catalog.ReadOriginal(template).ToArray();
            int variants = resource + TagView.FieldOffset<SWeaponHudConfigResource>(nameof(SWeaponHudConfigResource.FiringRows448Byte));
            TagView.Edit<uint>(broken, new TagArray(broken, variants).HeaderOffset + TagArray.ElementClassOffset) = SAnimationNameRow.Class;
            Refused(() => Prepare(broken), "Unexpected array class");
        }
        finally
        {
            catalog.ReadOverride = null; catalog.SetOverlayEntries([]);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /** The material template is read from the installation, so its texture row check is called directly. */
    private static void Material(PackageCatalog catalog)
    {
        var rows = typeof(WeaponPresentationAuthoring).Assembly.GetType("SunriseStudio.Authoring.OriginalAssetMaterials")!
            .GetMethod("TextureRows", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<TextureRows>();
        byte[] changed = catalog.ReadOriginal(0x815B8158).ToArray();
        var original = rows(changed);
        original[0].Texture ^= 1;
        Check(rows(changed).Length == 3, "An unrelated texture tag change was refused.");
        int field = TagView.OffsetOf(changed, in TagView.Checked<STechnique>(changed, 0).PixelShader.State.StaticTextures.Textures);
        TagView.Edit<uint>(changed, new TagArray(changed, field).HeaderOffset + TagArray.ElementClassOffset) = SAnimationNameRow.Class;
        Refused(() => rows(changed), "invalid texture array");
    }

    private static void Use(PackageCatalog catalog, uint tag, byte[] bytes) => catalog.ReadOverride = value => value == tag ? bytes : null;

    private static void Refused(Action action, string reason)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            Check(error.Message.Contains(reason, StringComparison.Ordinal), $"Refused for another reason: {error.Message}");
            return;
        }
        throw new InvalidOperationException($"A template layout break was accepted; expected \"{reason}\".");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
