using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Editor.Creation;

/** A new RGBA8 texture from a PNG, or new pixels for the open texture. */
public sealed class TextureWorkflow : CreationWorkflow
{
    private readonly StudioDocument? target;
    private readonly CreationField image, name, package;

    public TextureWorkflow(StudioWorkspace workspace, StudioDocument? edit = null)
    {
        target = edit;
        image = new("Image", CreationFieldKind.File, true, edit is null
            ? "A new texture header and pixel entry" : $"Pixels of texture {edit.Entry.TagHash:X8}")
        {
            FileFilter = "PNG image|*.png",
            Evidence = edit is null ? "TextureAssetImporter builds one 2D RGBA8 mip from an installed header."
                : "Import as delta applies PNG pixels with the texture's own layout."
        };
        name = new("Name", CreationFieldKind.Text, true, "Project asset name and package entry name");
        package = PackageField(workspace);
        name.Visible = package.Visible = edit is null;
    }

    public override string Title => target is null ? "New texture" : "Edit texture";
    public override IReadOnlyList<CreationField> Fields => [image, name, package];
    public override IReadOnlyList<string> Limits => target is null
        ? ["One mip, 2D, RGBA8."]
        : ["Same size as the texture; format kept."];

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (target is null && PackageProblem(workspace, package) is { } problem) yield return problem;
        if (target is not null && workspace.Document?.Entry.TagHash != target.Entry.TagHash)
            yield return "Open the texture again; the selection changed.";
        if (target is not null && SharesPixels(workspace, target)) yield return "This copy shares pixels with its original.";
    }

    /** A project texture may only write payloads the project adds; a copied header still names the original's. */
    private static bool SharesPixels(StudioWorkspace workspace, StudioDocument texture)
    {
        if (!workspace.IsProjectTag(texture.Entry.TagHash)) return false;
        if (!workspace.IsProjectTag(texture.Entry.Reference)) return true;
        if (texture.Texture is not { LargeMipCount: > 0 } || !TagView.Fits<TextureHeader>(texture.Bytes, 0)) return false;
        uint large = TagView.At<TextureHeader>(texture.Bytes).StreamedTag;
        return large is not (0 or TextureHeader.NoStreamedTag) && !workspace.IsProjectTag(large);
    }

    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        string path = image.Text.Trim();
        if (target is { Texture: { } texture })
        {
            workspace.ApplyAssetEdits(TextureEdits.Apply(workspace.Catalog!, target.Entry.TagHash,
                PngInterchange.ToTexture(PngInterchange.Read(path), texture)), "Texture edited");
            return new(Status: "Texture edited");
        }
        string assetName = name.Text.Trim();
        ushort packageId = PackageId(package);
        int first = workspace.NextImportEntry(packageId);
        workspace.ImportAsset((project, catalog) =>
            new(TextureAssetImporter.PrepareRgba(catalog, PngInterchange.Read(path), assetName, packageId, first).Assets));
        return new(Background: true, Status: "Texture created");
    }
}
