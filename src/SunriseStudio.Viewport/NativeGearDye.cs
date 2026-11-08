using System.Numerics;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Viewport;

/** Extern 86 vector 0 is the low bit of the part's dye slot, splatted; 0x7FF742E71450 writes it. */
public static class NativeGearDye
{
    public static readonly TfxExternKey Key = new(86, 0);
    // The dye slot is the pair index times two plus the bit.
    private static readonly int PartSlotByte = TagView.FieldOffset<SEntityModelPart>(nameof(SEntityModelPart.DyeSlot));

    /** The extern starts at zero, so a part that is not an entity part row reads bit 0; the cloth draw 0x7FF742E78D60 writes 0. */
    public static int PartBit(MeshInstance instance) =>
        !instance.RequiresClothSimulation && instance.Mesh.Native is { Kind: NativeMeshKind.Entity, SourceHeader: { } model }
            && instance.Mesh.PartIndex >= 0 && instance.Mesh.PartIndex + PartSlotByte < model.Length
            ? model[instance.Mesh.PartIndex + PartSlotByte] & 1 : 0;

    public static bool Reads(MaterialExecutionData execution) =>
        new[] { execution.Vertex, execution.Pixel }.Any(stage => stage.TfxBytecode.Length != 0
            && TfxProgramInspection.Read(stage.TfxBytecode).Dependencies.Any(d => d.Extern == Key.Extern));

    public static void Apply(TfxInputs inputs, int bit) => inputs.Vectors[Key] = new Vector4(bit);
}
