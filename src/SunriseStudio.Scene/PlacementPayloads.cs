using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

/** The tag a placement row's payload names, read through the payload class's own view. */
internal static class PlacementPayloads
{
    /**
     * Only classes whose +16 word names a tag are read; on the other classes it is not a tag.
     * @param payload Where the payload starts, after its class word.
     * @return The named tag; 0 for another class or when the word is outside the tag.
     */
    public static uint Tag(ReadOnlySpan<byte> tag, int payload, uint payloadClass) => payloadClass switch
    {
        C80806b59.Class => Word<C80806b59>(tag, payload, nameof(C80806b59.Unk10)),
        C80806b5b.Class => Word<C80806b5b>(tag, payload, nameof(C80806b5b.Unk10)),
        C80806b87.Class => Word<C80806b87>(tag, payload, nameof(C80806b87.Unk10)),
        SLensFlarePayload.Class => Word<SLensFlarePayload>(tag, payload, nameof(SLensFlarePayload.LensFlare)),
        UnknownPlacedModelPayload.Class => Word<UnknownPlacedModelPayload>(tag, payload, nameof(UnknownPlacedModelPayload.Model)),
        C80806df1.Class => Word<C80806df1>(tag, payload, nameof(C80806df1.Unk10)),
        C80806e62.Class => Word<C80806e62>(tag, payload, nameof(C80806e62.Unk10)),
        SAmbientOcclusionPayload.Class => Word<SAmbientOcclusionPayload>(tag, payload, nameof(SAmbientOcclusionPayload.OcclusionMap)),
        SLightsPayload.Class => Word<SLightsPayload>(tag, payload, nameof(SLightsPayload.LightCollection)),
        UnknownTimeOfDayPayload.Class => Word<UnknownTimeOfDayPayload>(tag, payload, nameof(UnknownTimeOfDayPayload.CurveSet)),
        C80806f78.Class => Word<C80806f78>(tag, payload, nameof(C80806f78.Unk10)),
        SSkyPayload.Class => Word<SSkyPayload>(tag, payload, nameof(SSkyPayload.SkyObjects)),
        C80806fa6.Class => Word<C80806fa6>(tag, payload, nameof(C80806fa6.Unk10)),
        SVolumePayload.Class => Word<SVolumePayload>(tag, payload, nameof(SVolumePayload.Unk10)),
        C80807133.Class => Word<C80807133>(tag, payload, nameof(C80807133.Unk10)),
        UnknownDecoratorPlacementPayload.Class => Word<UnknownDecoratorPlacementPayload>(tag, payload,
            nameof(UnknownDecoratorPlacementPayload.DecoratorPayload)),
        SStaticInstancesPayload.Class => Word<SStaticInstancesPayload>(tag, payload, nameof(SStaticInstancesPayload.StaticOwner)),
        SPlacementVisibilityConstructor.Class => Word<SPlacementVisibilityConstructor>(tag, payload,
            nameof(SPlacementVisibilityConstructor.VisibilityMap)),
        C8080859b.Class => Word<C8080859b>(tag, payload, nameof(C8080859b.Unk10)),
        C8080899c.Class => Word<C8080899c>(tag, payload, nameof(C8080899c.Unk10)),
        C808089cd.Class => Word<C808089cd>(tag, payload, nameof(C808089cd.Unk10)),
        SStaticCollisionPayload.Class => Word<SStaticCollisionPayload>(tag, payload, nameof(SStaticCollisionPayload.CollisionOwner)),
        SSpawnPointsPayload.Class => Word<SSpawnPointsPayload>(tag, payload, nameof(SSpawnPointsPayload.SpawnPoints)),
        C8080929a.Class => Word<C8080929a>(tag, payload, nameof(C8080929a.Unk10)),
        TriggerShapeReference.Class => Word<TriggerShapeReference>(tag, payload, nameof(TriggerShapeReference.Resource)),
        C80809b48.Class => Word<C80809b48>(tag, payload, nameof(C80809b48.Unk10)),
        _ => 0
    };

    /** @return The u32 `member` of the T at `payload`, or 0 when it is outside the tag. */
    private static uint Word<T>(ReadOnlySpan<byte> tag, int payload, string member) where T : unmanaged =>
        TagView.FitsThrough<T>(tag, payload, member) ? TagView.Member<T, uint>(tag, payload, member) : 0;
}
