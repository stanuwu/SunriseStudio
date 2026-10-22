using SunriseStudio.Formats;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    private readonly object guardianLock = new();
    private GuardianAssembler? guardianAssembler;

    /** True exports every body clip with a guardian; false exports the weapon hold pose alone. */
    public bool GuardianExportAllClips { get; set; } = true;

    /** The race/gender tables of the open catalog; null until a guardian view has read them. */
    public GuardianAppearance? GuardianAppearance =>
        guardianAssembler is { } held && ReferenceEquals(held.Catalog, Catalog) ? held.AppearanceIfRead : null;

    /**
     * Opens a class body with this loadout; every pick is its own place in the history.
     * A new loadout on the open body keeps the old guardian on screen until the new one is ready.
     */
    public void OpenGuardian(uint bodyTag, GuardianLoadout loadout)
    {
        var context = new AssetOpenContext { Guardian = loadout };
        if (IsBusy || Document is not { Guardian: not null } open || open.Entry.TagHash != bodyTag) { Open(bodyTag, context); return; }
        if (!SameView(open.OpenContext, context)) SelectAsset(bodyTag, context, keepView: true);
    }

    /** One assembler per catalog; it keeps the body rigs, which are slow to read. */
    private GuardianAssembler GuardianAssemblerFor(PackageCatalog catalog)
    {
        lock (guardianLock)
        {
            if (guardianAssembler is { } held && ReferenceEquals(held.Catalog, catalog)) return held;
            return guardianAssembler = new(catalog) { BoneNames = ResolveHash, ClipNames = ResolveClipName };
        }
    }

    /** Armor previews draw their wearer, not only the piece, and weapons open in a guardian's hands; reopen to apply. */
    public bool ShowGuardian { get; set; }

    /**
     * Runs on a worker. An armor item opens worn by the wearer the model picker chose, the piece alone unless
     * ShowGuardian is set. An armor item without a readable model still opens, with the reason as a preview note.
     */
    private StudioDocument ReadArmorModelDocument(PackageCatalog catalog, PackageEntry entry, string name,
        AssetOpenContext context, StudioDocument document) =>
        ReadGuardianItemDocument(catalog, entry, name, context, document, "No armor model",
            assembler => assembler.Wearers(entry.TagHash),
            (assembler, wearer) => assembler.PrepareItem(entry.TagHash, wearer, OperationToken, ShowGuardian));

    /**
     * Runs on a worker. A shader or a weapon opens on a guardian, the one the model picker chose: a shader's dyes colour
     * showcase armor, and a weapon is held in its hold pose.
     * @param dyes The shader's dyes; null keeps each armor piece's own.
     * @param weapon The weapon item the guardian holds; 0 holds none.
     */
    private StudioDocument ReadShownOnGuardianDocument(PackageCatalog catalog, PackageEntry entry, string name,
        AssetOpenContext context, StudioDocument document, NativeGearDyes? dyes, uint weapon = 0) =>
        ReadGuardianItemDocument(catalog, entry, name, context, document, "No guardian", _ => GuardianAssembler.AllWearers,
            (assembler, wearer) => assembler.PrepareWearer(wearer ?? GuardianAssembler.AllWearers[0], dyes, OperationToken, weapon));

    /**
     * An item on the wearer whose class body and gender the context names, else the first; the preview note gives the
     * reason a guardian did not assemble.
     */
    private StudioDocument ReadGuardianItemDocument(PackageCatalog catalog, PackageEntry entry, string name,
        AssetOpenContext context, StudioDocument document, string failure, Func<GuardianAssembler, IReadOnlyList<ArmorWearer>> list,
        Func<GuardianAssembler, ArmorWearer?, PreparedGuardian> prepare)
    {
        PreparedGuardian prepared;
        IReadOnlyList<ArmorWearer> wearers;
        try
        {
            ReportProgress("Assembling guardian");
            var assembler = GuardianAssemblerFor(catalog);
            wearers = list(assembler);
            var wearer = wearers.FirstOrDefault(value => value.Class.BodyTag == context.EntityTag
                && value.Female == context.WearerFemale);
            prepared = prepare(assembler, wearer);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            return document with { OpenContext = context, PreviewError = $"{failure}: {error.Message}" };
        }
        var scene = prepared.Scene;
        Names?.NameScene(scene);
        scene.Name = name;
        var native = PrepareScene(scene, out string nativeError, assetPreview: true);
        return document with
        {
            Scene = scene, Animation = prepared.Animation, Wearers = wearers, NativePlan = native, NativeError = nativeError,
            OpenContext = context,
            Surface = AssetSurface.Read(catalog, entry.TagHash, scene: scene, nameResolver: ResolveName,
                token: OperationToken, decodedTextures: native?.Textures),
            SourceTags = (document.SourceTags ?? new HashSet<uint>()).Concat(prepared.SourceTags).ToHashSet()
        };
    }

    /** Runs on a worker. A failed assembly keeps the body open with the reason as a preview note. */
    private StudioDocument ReadGuardianDocument(PackageCatalog catalog, PackageEntry entry, string name, AssetOpenContext context)
    {
        if (GuardianClasses.ForBody(entry.TagHash) is not { } body)
            return ReadDocument(catalog, entry, name, false, context with { Guardian = null })
                with { OpenContext = context, PreviewError = "Choose a guardian class body." };
        name = body.DisplayName;
        var document = ReadDocument(catalog, entry, name, false, context with { Guardian = null });
        var assembler = GuardianAssemblerFor(catalog);
        PreparedGuardian prepared;
        try
        {
            ReportProgress("Reading guardian tables");
            _ = assembler.Appearance;
            ReportProgress("Assembling guardian");
            prepared = assembler.Prepare(entry.TagHash, context.Guardian!, OperationToken);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            return document with { OpenContext = context, PreviewError = $"Guardian assembly failed: {error.Message}" };
        }
        var scene = prepared.Scene;
        Names?.NameScene(scene);
        scene.Name = name;
        var native = PrepareScene(scene, out string nativeError, assetPreview: true);
        ReportProgress("Reading materials and shaders");
        return document with
        {
            Scene = scene, Animation = prepared.Animation, NativePlan = native, NativeError = nativeError,
            OpenContext = context, Guardian = prepared,
            // The guardian's notes show once, on the Guardian page.
            Connections = new(entry.TagHash, entry.TagHash, entry.TagHash, 0, GuardianRelated(document, prepared),
                [entry.TagHash], []),
            Surface = AssetSurface.Read(catalog, entry.TagHash, scene: scene, nameResolver: ResolveName,
                token: OperationToken, decodedTextures: native?.Textures),
            SourceTags = (document.SourceTags ?? new HashSet<uint>()).Concat(prepared.SourceTags).Append(entry.TagHash).ToHashSet()
        };
    }

    /** The body's own links, then each item, each part entity, each model and the weapon's hold pose. */
    private List<RelatedAsset> GuardianRelated(StudioDocument document, PreparedGuardian prepared)
    {
        uint body = document.Entry.TagHash;
        var related = document.Connections?.Related.ToList() ?? [];
        foreach (var part in prepared.Parts.Parts.Where(part => part.ItemTag != 0).DistinctBy(part => part.ItemTag))
            related.Add(new(part.ItemTag, DisplayName(part.ItemTag), AssetRelationshipKind.Reference, $"{part.Label} item", body, -1));
        foreach (var part in prepared.Parts.Parts.Where(part => part.EntityTag != body).DistinctBy(part => part.EntityTag))
            related.Add(new(part.EntityTag, DisplayName(part.EntityTag), AssetRelationshipKind.Component, $"{part.Label} entity",
                part.ItemTag != 0 ? part.ItemTag : body, -1));
        foreach (var model in prepared.Scene.Instances.DistinctBy(instance => instance.Mesh.TagHash))
            related.Add(new(model.Mesh.TagHash, DisplayName(model.Mesh.TagHash), AssetRelationshipKind.Model,
                "Guardian model", model.ModelSourceConfigTag, -1,
                new(model.ObjectClassTag, model.Mesh.TagHash, model.ModelSourceConfigTag)));
        if (prepared.PoseClipTag != 0)
            related.Add(new(prepared.PoseClipTag, DisplayName(prepared.PoseClipTag), AssetRelationshipKind.AnimationClip,
                "Weapon hold pose", body, -1));
        return related;
    }

    /**
     * One armature with every part on it; the weapon follows the grip bone. View only.
     * @param allClips True adds every body clip after the hold pose; false exports the hold pose alone.
     * @return Null when the body rig did not read.
     */
    public static AssetBundleAssembly? GuardianAssembly(string name, uint bodyTag, PreparedGuardian guardian,
        AssetOpenContext? context, bool allClips = true)
    {
        if (guardian.Animation is not { } animation) return null;
        var attached = new Dictionary<SunriseStudio.Animation.ModelSkinBinding, int>(ReferenceEqualityComparer.Instance);
        foreach (var (skin, attachment) in animation.AttachedSkins) attached[skin] = attachment.Bone;
        var clips = new List<uint>();
        if (guardian.PoseClipTag != 0) clips.Add(guardian.PoseClipTag);
        if (allClips) clips.AddRange(animation.Clips.Select(clip => clip.ClipTag).Distinct().Where(tag => tag != guardian.PoseClipTag));
        return new AssetBundleAssembly(name, bodyTag, guardian.Scene, animation.Rig, animation.SkinBindings, attached, clips, context)
            { RestBinding = animation.Binding, ClipNames = animation.ClipNames };
    }

    /** Exports the open guardian; GuardianExportAllClips picks every clip or the hold pose alone. */
    private void ExportGuardianBundle(string folder, StudioDocument document, PreparedGuardian guardian, PackageCatalog catalog,
        AssetBundleExportOptions options)
    {
        var assembly = GuardianAssembly(document.Name, document.Entry.TagHash, guardian, document.OpenContext, GuardianExportAllClips)
            ?? throw new InvalidOperationException("The body rig did not read, so this guardian cannot export.");
        BeginWork("Exporting Blender guardian", () =>
        {
            // The open document's plan gave its textures to the renderer, so the plate atlases are composed again.
            ReportProgress("Composing dyes and plate textures");
            var plan = AssetViewport.PrepareNativeScene(catalog, guardian.Scene, OperationToken, ReportProgress,
                textureBudgetBytes: long.MaxValue, assetPreview: true);
            var result = AssetBundle.ExportAssembly(catalog, assembly with { ShaderInputs = GuardianShaderInputs.Read(plan) },
                folder, options, OperationToken, ReportProgress);
            return () =>
            {
                LastAssetExport = result; LastTransferWasExport = true; TransferRevision++;
                Status = "Blender guardian exported";
            };
        });
    }
}
