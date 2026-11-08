using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SunriseStudio.Tags.Views;

/** Member offsets from a struct's layout, and reads of one member. */
public static partial class TagView
{
    private static readonly ConcurrentDictionary<(Type Type, string Member), (int Offset, int Size)> Members = new();

    /**
     * @param member A field name; a dotted path reaches into nested structs and `Name[n]` into an inline array.
     * @return The member's offset inside T; ArgumentException when T has no such member.
     */
    public static int FieldOffset<T>(string member) where T : unmanaged => MemberRange(typeof(T), member).Offset;

    /** @return True when the bytes from `offset` through the end of `member` of a T at `offset` lie inside the tag. */
    public static bool FitsThrough<T>(ReadOnlySpan<byte> tag, int offset, string member) where T : unmanaged
    {
        var (at, size) = MemberRange(typeof(T), member);
        return offset >= 0 && (long)offset + at + size <= tag.Length;
    }

    /** @return `member` of a T at `offset`, as a reference into the tag. */
    public static ref readonly M Member<T, M>(ReadOnlySpan<byte> tag, int offset, string member) where T : unmanaged where M : unmanaged =>
        ref At<M>(tag, offset + FieldOffset<T>(member));

    /** As Member; only the member's bytes must fit, else InvalidDataException. */
    public static ref readonly M CheckedMember<T, M>(ReadOnlySpan<byte> tag, int offset, string member) where T : unmanaged where M : unmanaged
    {
        long at = (long)offset + FieldOffset<T>(member);
        if (offset < 0 || at > tag.Length - Unsafe.SizeOf<M>())
            throw new InvalidDataException($"{typeof(T).Name}.{member} at {offset} does not fit the {tag.Length}-byte tag.");
        return ref At<M>(tag, (int)at);
    }

    private static (int Offset, int Size) MemberRange(Type type, string member) =>
        Members.GetOrAdd((type, member), static key => Resolve(key.Type, key.Member));

    private static (int Offset, int Size) Resolve(Type type, string member)
    {
        int offset = 0;
        var current = type;
        foreach (string part in member.Split('.'))
        {
            int open = part.IndexOf('[');
            string name = open < 0 ? part : part[..open];
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            // A record struct keeps each positional member in a backing field.
            var field = current.GetField(name, any) ?? current.GetField($"<{name}>k__BackingField", any)
                ?? throw new ArgumentException($"{current.Name} has no member \"{name}\".", nameof(member));
            offset += OffsetIn(current, field);
            current = field.FieldType;
            if (open < 0) continue;
            if (!part.EndsWith(']') || !int.TryParse(part.AsSpan(open + 1, part.Length - open - 2), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int index))
                throw new ArgumentException($"\"{part}\" is not a member index.", nameof(member));
            int length = current.GetCustomAttribute<InlineArrayAttribute>()?.Length
                ?? throw new ArgumentException($"{name} is not an inline array.", nameof(member));
            if (index >= length) throw new ArgumentException($"{name} holds {length} values, not {index + 1}.", nameof(member));
            current = Fields(current)[0].FieldType;
            offset += index * SizeOf(current);
        }
        return (offset, SizeOf(current));
    }

    /** Explicit layouts name each offset; sequential ones pack each field to its alignment, capped by Pack. */
    private static int OffsetIn(Type type, FieldInfo field)
    {
        if (field.GetCustomAttribute<FieldOffsetAttribute>() is { } explicitOffset) return explicitOffset.Value;
        int pack = PackOf(type), offset = 0;
        foreach (var each in Fields(type))
        {
            int align = Math.Min(pack, Alignment(each.FieldType));
            offset = (offset + align - 1) / align * align;
            if (each == field) return offset;
            offset += SizeOf(each.FieldType);
        }
        throw new ArgumentException($"{field.Name} is not a field of {type.Name}.");
    }

    private static int Alignment(Type type)
    {
        if (type.IsPrimitive) return SizeOf(type);
        if (type.IsEnum) return Alignment(Enum.GetUnderlyingType(type));
        int align = 1;
        foreach (var field in Fields(type)) align = Math.Max(align, Alignment(field.FieldType));
        return Math.Min(align, PackOf(type));
    }

    /** Zero Pack means the default, 8. */
    private static int PackOf(Type type) => type.StructLayoutAttribute?.Pack is > 0 and var pack ? pack : 8;

    private static FieldInfo[] Fields(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy(x => x.MetadataToken).ToArray();

    private static int SizeOf(Type type) => RuntimeHelpers.SizeOf(type.TypeHandle);
}
