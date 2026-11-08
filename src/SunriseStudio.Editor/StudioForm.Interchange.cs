using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** Converted files are working copies; only their imported differences enter a project. */
    private void ExportSelected()
    {
        if (workspace.Document is not { } document || workspace.IsBusy) return;
        var selectedPart = selectedMesh is { } part && document.Scene?.Instances.Contains(part) == true ? part : null;
        string name = $"{document.Entry.TagHash:X8}";
        string filter = "Raw asset|*.bin|Properties|*.json";
        if (document.Texture is not null) filter = "DDS texture|*.dds|PNG image|*.png|" + filter;
        else if (document.Scene?.Instances.Count > 0) filter = "glTF binary|*.glb|glTF scene|*.gltf|Wavefront mesh|*.obj|" + filter;
        SaveFile(filter, name, path => workspace.Execute("Exporting asset", () =>
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".dds" when document.Texture is { } texture:
                    DdsInterchange.Write(path, texture); break;
                case ".png" when document.Texture is { } texture:
                    PngInterchange.Write(path, TexturePixels.Decode(texture)); break;
                case ".obj" when document.Scene is { } scene:
                    if (selectedPart is { } selected) ObjInterchange.Write(path, selected.Mesh);
                    else ObjInterchange.Write(path, scene);
                    break;
                case ".glb" or ".gltf" when document.Scene is { } scene:
                    if (selectedPart is { } gltfPart) GltfInterchange.Write(path, gltfPart.Mesh);
                    else GltfInterchange.Write(path, scene);
                    break;
                case ".json":
                    PropertyInterchange.Write(path, Context(document), document.Inspection); break;
                default: File.WriteAllBytes(path, document.Bytes); break;
            }
        }));
    }

    private void ImportDelta()
    {
        if (!workspace.CanEdit || workspace.Document is not { } document) return;
        PickFile("Supported assets|*.bin;*.dds;*.png;*.obj;*.gltf;*.glb;*.json|All files|*.*", path =>
        {
            if (document.AddedAssetId is { } id && workspace.Project!.AddedAssets.First(asset => asset.Id == id).Native is null)
            {
                workspace.ApplyBytes(File.ReadAllBytes(path));
                return;
            }
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".dds" when document.Texture is not null:
                    workspace.ApplyAssetEdits(TextureEdits.Apply(workspace.Catalog!, document.Entry.TagHash,
                        DdsInterchange.Read(path))); break;
                case ".png" when document.Texture is { } texture:
                    workspace.ApplyAssetEdits(TextureEdits.Apply(workspace.Catalog!, document.Entry.TagHash,
                        PngInterchange.ToTexture(PngInterchange.Read(path), texture))); break;
                case ".obj" or ".gltf" or ".glb" when document.Scene is { } scene:
                    ImportMeshPositions(path, scene); break;
                case ".json":
                    if (path.EndsWith(".sunrise-material.json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (MaterialInterchange.ReadMaterialTag(path) != document.Entry.TagHash)
                            throw new InvalidDataException("Select the material that matches this file.");
                        workspace.ImportEdits(() => MaterialInterchange.Import(workspace.Catalog!, path));
                        break;
                    }
                    workspace.ApplyBytes(PropertyInterchange.Apply(path, Context(document), document.Inspection)); break;
                case ".dds" or ".png" or ".obj" or ".gltf" or ".glb":
                    throw new NotSupportedException("This file format cannot replace the selected asset.");
                default:
                    workspace.ApplyBytes(File.ReadAllBytes(path)); break;
            }
        });
    }

    /** The per-part export keeps source vertex order for native position reimport. */
    private void ImportMeshPositions(string path, SceneDocument scene)
    {
        var parts = selectedMesh is { } selected && scene.Instances.Contains(selected) ? [selected.Mesh]
            : scene.Instances.Select(instance => instance.Mesh).Distinct().ToArray();
        var imported = Path.GetExtension(path).Equals(".obj", StringComparison.OrdinalIgnoreCase)
            ? ObjInterchange.Read(path) : GltfInterchange.Read(path);
        if (parts.Length != 1 || imported.Count != 1)
            throw new NotSupportedException("Select one mesh part for position reimport.");
        workspace.ApplyAssetEdits(NativeMeshEdits.ApplyPositions(parts[0], imported[0]));
    }

    private AssetInspectionContext Context(StudioDocument document) =>
        new(document.Entry.TagHash, document.Entry.Reference, document.Bytes, document.Name,
            workspace.Catalog!.GetName, workspace.Catalog!.ContainsTag, workspace.Catalog!.Read);

    private void ExportAdded(AddedProjectAsset asset) =>
        SaveFile("All files|*.*", asset.Name, path =>
            File.WriteAllBytes(path, workspace.Project!.ReadAddedAsset(asset.Id, workspace.Catalog)));

    private void ExportAnimation()
    {
        if (AssetPreviewViewport?.Animation is not { Source.Binding: { } binding, Clip: { } clip } animation) return;
        SaveFile("glTF animation|*.gltf", $"animation-{clip.TagHash:X8}.gltf", path =>
            workspace.Execute("Exporting animation", () =>
                AnimationInterchange.WriteGltf(path, animation.Source.Rig, binding, clip)));
    }

    private uint? SelectedClipTag() => AssetPreviewViewport?.Animation?.Clip?.TagHash
        ?? (workspace.Document?.Entry.Reference == SAnimationClip.Class ? workspace.Document.Entry.TagHash : null);

    private void ExportAnimationControls()
    {
        if (SelectedClipTag() is not { } tag) return;
        SaveFile("Editable glTF animation|*.gltf", $"controls-{tag:X8}.gltf", path =>
            workspace.Execute("Exporting animation controls", () =>
                AnimationAssetImporter.ExportControlsGltf(workspace.Catalog!, tag, path)));
    }

    private void ImportAnimationControls()
    {
        if (SelectedClipTag() is not { } tag) return;
        PickFile("glTF animation|*.gltf;*.glb", path => workspace.ImportEdits(() =>
        {
            var imported = AnimationAssetImporter.PrepareGltf(workspace.Catalog!, tag, path);
            return new Dictionary<uint, byte[]> { [tag] = imported.Data };
        }));
    }

    private void ExportMaterialBundle()
    {
        if (workspace.Document is not { } document) return;
        PickFolder("Empty material output folder", folder =>
        {
            EnsureExternalExport(Path.Combine(folder, "material.sunrise-material.json"));
            workspace.Execute("Exporting Blender material", () =>
                MaterialInterchange.Export(workspace.Catalog!, document.Entry.TagHash, folder));
        });
    }
}
