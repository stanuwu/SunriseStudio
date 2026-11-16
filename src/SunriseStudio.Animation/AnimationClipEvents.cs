using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Animation;

/** One clip event: the payload an event slot points at. A kind this code does not make keeps its bytes. */
public sealed class ClipEvent
{
    /** Kind of a tagged event: it posts a sound event or spawns an effect entity. */
    public const int TaggedKind = 3;

    internal ClipEvent(TagBlueprint payload) => Payload = payload;

    internal TagBlueprint Payload { get; }
    public uint ClassId => Payload.ClassId;
    public int Frame => Head().Frame;
    public int Kind => Head().Kind;
    /** The sound event or effect entity of a tagged event; zero for another class. */
    public uint Target => Tagged is { } value ? value.EventTarget : 0;
    /** The point of a tagged event; the empty hash means the owner's own transform. */
    public uint AttachmentPoint => Tagged is { } value ? value.AttachmentPoint : StringHash.Empty;
    /** The source path a tagged event carries, if any. */
    public string? Path => Payload.Links.FirstOrDefault(link => link.Kind == TagLinkKind.String
        && link.Field == TagView.FieldOffset<AnimationTagEvent>(nameof(AnimationTagEvent.AudioPath)))?.Text is { } text
        ? Encoding.ASCII.GetString(text.AsSpan(0, text.Length - 1)) : null;

    private AnimationTagEvent? Tagged => ClassId == AnimationTagEvent.Class ? As<AnimationTagEvent>() : null;

    /**
     * A new tagged event.
     * @param target A sound event or an effect entity.
     * @param attachmentPoint Point name hash on the owner; the empty hash uses the owner's transform.
     * @param path Source path text; installed events carry one, the game was not seen to need it.
     * @param detached True spawns the effect as its own object.
     */
    public static ClipEvent NewTagged(int frame, uint target, uint attachmentPoint = StringHash.Empty, string? path = null,
        bool detached = false)
    {
        byte[] bytes = new byte[Unsafe.SizeOf<AnimationTagEvent>()];
        ref var value = ref MemoryMarshal.AsRef<AnimationTagEvent>(bytes);
        value.Frame = checked((short)frame);
        value.EventKind = TaggedKind;
        value.DetachedSpawn = (byte)(detached ? 1 : 0);
        value.AttachmentPoint = attachmentPoint;
        value.EventTarget = target;
        var payload = new TagBlueprint { ClassId = AnimationTagEvent.Class, Bytes = bytes };
        if (path is not null)
            payload.Links.Add(new(TagView.FieldOffset<AnimationTagEvent>(nameof(AnimationTagEvent.AudioPath)),
                TagLinkKind.String, 0, [], Encoding.ASCII.GetBytes(path + "\0")));
        return new(payload);
    }

    /** Every event class starts with its frame and kind. */
    private (short Frame, byte Kind) Head() => ClassId switch
    {
        AnimationTagEvent.Class => (As<AnimationTagEvent>().Frame, As<AnimationTagEvent>().EventKind),
        AnimationEventKind2.Class => (As<AnimationEventKind2>().Frame, As<AnimationEventKind2>().EventKind),
        AnimationEventKind4.Class => (As<AnimationEventKind4>().Frame, As<AnimationEventKind4>().EventKind),
        AnimationEventKind5.Class => (As<AnimationEventKind5>().Frame, As<AnimationEventKind5>().EventKind),
        AnimationEventKind6.Class => (As<AnimationEventKind6>().Frame, As<AnimationEventKind6>().EventKind),
        _ => throw new NotSupportedException($"Clip event class 0x{ClassId:X8} is not known."),
    };

    private T As<T>() where T : unmanaged => MemoryMarshal.Read<T>(Payload.Bytes);
}

/** Reads and writes the event list of a native clip. */
public static class AnimationClipEvents
{
    private static readonly int EventsField = TagView.FieldOffset<SAnimationClip>(nameof(SAnimationClip.Events));
    private static readonly int SlotField = TagView.FieldOffset<SAnimationEventSlot>(nameof(SAnimationEventSlot.FrameEvent));

    /** @return The clip's events in stored order; a clip the canonical writer cannot place is refused. */
    public static IReadOnlyList<ClipEvent> Read(byte[] clip, RegistryIndex schema) => Events(Blueprint(clip, schema, true));

    /**
     * Replaces the clip's event list. Events are stored by frame; events on one frame keep their order.
     * @param compact True drops bytes no field reaches; a compiled clip keeps its template's old section there.
     * @return The clip laid out again; every other array and object keeps its content.
     */
    public static byte[] Write(byte[] clip, RegistryIndex schema, IEnumerable<ClipEvent> events, bool compact = false)
    {
        var root = Blueprint(clip, schema, compact);
        var ordered = events.OrderBy(value => value.Frame).ToArray();
        int existing = root.Links.FindIndex(link => link.Field == EventsField);
        // A clip that never had an event array keeps its empty field.
        if (existing < 0 && ordered.Length == 0) return CanonicalTagWriter.Write(root, schema);
        if (existing >= 0) root.Links.RemoveAt(existing);
        var rows = new List<TagBlueprint>(ordered.Length);
        foreach (var value in ordered)
        {
            var row = new TagBlueprint { ClassId = SAnimationEventSlot.Class, Bytes = new byte[Unsafe.SizeOf<SAnimationEventSlot>()] };
            row.Links.Add(new(SlotField, TagLinkKind.Object, value.ClassId, [value.Payload]));
            rows.Add(row);
        }
        root.Links.Add(new(EventsField, TagLinkKind.Array, SAnimationEventSlot.Class, rows));
        return CanonicalTagWriter.Write(root, schema);
    }

    private static TagBlueprint Blueprint(byte[] clip, RegistryIndex schema, bool compact = false) =>
        TagBlueprintReader.Read(TagDocument.Parse(clip, SAnimationClip.Class, schema), schema, compact);

    private static IReadOnlyList<ClipEvent> Events(TagBlueprint root)
    {
        if (root.Links.FirstOrDefault(link => link.Field == EventsField) is not { } array) return [];
        var result = new List<ClipEvent>(array.Children.Count);
        foreach (var row in array.Children)
        {
            if (row.Links.SingleOrDefault(link => link.Field == SlotField && link.Kind == TagLinkKind.Object) is not { } slot)
                throw new NotSupportedException("A clip event slot is empty.");
            result.Add(new(slot.Children[0]));
        }
        return result;
    }
}
