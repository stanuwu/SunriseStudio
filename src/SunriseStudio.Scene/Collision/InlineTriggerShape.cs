using System.Numerics;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene.Collision;

/** The inline shape's axis can keep its rotation fixed in world space. */
public sealed record InlineTriggerShape(CollisionShape Shape, bool WorldAligned)
{
    /** Kinds above this name a packfile instead of an inline shape. */
    internal const byte LastInlineKind = 3;

    public Matrix4x4 WorldTransform(Matrix4x4 owner) => WorldAligned
        ? Matrix4x4.CreateTranslation(owner.Translation) : owner;

    /** Every shape variant starts with its kind byte. */
    internal static byte Kind(byte[] bytes, int offset) =>
        TagRead.U8(bytes, offset + TagView.FieldOffset<TriggerShapeSphere>(nameof(TriggerShapeSphere.Kind)));

    /** Native kinds 0 and cylinder axis 0 ignore the owner's rotation. */
    public static InlineTriggerShape Decode(byte[] bytes, int offset)
    {
        byte kind = Kind(bytes, offset);
        switch (kind)
        {
            case 0:
                return new(CollisionShapes.BoxShape(TagView.Member<TriggerShapeBoxWorldAligned, Vector3>(bytes, offset,
                    nameof(TriggerShapeBoxWorldAligned.BoxSize)) * 0.5f), true);
            case 1:
                return new(CollisionShapes.BoxShape(TagView.Member<TriggerShapeBox, Vector3>(bytes, offset,
                    nameof(TriggerShapeBox.BoxSize)) * 0.5f), false);
            case 2:
                return new(CollisionShapes.SphereShape(Vector3.Zero, TagView.Member<TriggerShapeSphere, float>(bytes, offset,
                    nameof(TriggerShapeSphere.Radius))), false);
            case 3:
            {
                float radius = TagView.Member<TriggerShapeCylinder, float>(bytes, offset, nameof(TriggerShapeCylinder.Radius));
                float length = TagView.Member<TriggerShapeCylinder, float>(bytes, offset, nameof(TriggerShapeCylinder.Length));
                float split = TagView.Member<TriggerShapeCylinder, float>(bytes, offset, nameof(TriggerShapeCylinder.Split));
                byte axis = TagView.Member<TriggerShapeCylinder, byte>(bytes, offset, nameof(TriggerShapeCylinder.Axis));
                if (!float.IsFinite(radius) || !float.IsFinite(length) || !float.IsFinite(split) || radius <= 0 || length <= 0)
                    throw new InvalidDataException("The inline cylinder has invalid dimensions.");
                var direction = axis == 1 ? Vector3.UnitX : Vector3.UnitZ;
                return new(CollisionShapes.CylinderShape(-direction * (length * split), direction * (length * (1 - split)), radius), axis == 0);
            }
            default:
                throw new NotSupportedException("This inline shape refers to a packfile.");
        }
    }
}
