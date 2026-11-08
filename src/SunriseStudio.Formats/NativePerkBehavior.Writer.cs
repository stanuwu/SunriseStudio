using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Formats;

public static partial class NativePerkBehavior
{
    /** Arrays and payload markers are appended so existing relative pointers keep their targets. */
    private sealed class PerkWriter
    {
        private byte[] bytes;
        public PerkWriter(byte[] initial) => bytes = (byte[])initial.Clone();
        /** The program's own tag, stored by damage-resist self references; 0 leaves them unchanged. */
        public uint ProgramTag { get; init; }

        public int Node(uint type, int size) => TagRelocator.AppendObject(ref bytes, type, size);

        public int Array(int field, uint type, int count, int stride)
        {
            if (count == 0) { bytes.AsSpan(field, TagArray.FieldSize).Clear(); return 0; }
            return TagRelocator.AppendArray(ref bytes, field, type, count, checked(count * stride));
        }

        /** An inline string: u64 length with the terminator, then the bytes; pointers target the bytes. */
        public int String(string text)
        {
            if (strings.TryGetValue(text, out int known)) return known;
            byte[] data = Ascii(text);
            int header = Node(StringClass, Unsafe.SizeOf<SInlineString>() + data.Length);
            Edit<SInlineString>(header).ByteLength = (ulong)data.Length;
            int start = header + Unsafe.SizeOf<SInlineString>();
            Copy(start, data);
            strings[text] = start;
            return start;
        }

        // One copy of each string per compile; label rows and sets all point at it.
        private readonly Dictionary<string, int> strings = new(StringComparer.Ordinal);

        public byte[] Finish()
        {
            Edit<SSandboxPerkProgram>(0).TagSize = (ulong)bytes.Length;
            return bytes;
        }

        /** The struct at `at`; Node, Array and String move the bytes and void the reference. */
        public ref T Edit<T>(int at) where T : unmanaged => ref TagView.Edit<T>(bytes, at);

        public void Copy(int at, byte[] data) => data.CopyTo(bytes, at);
        public void U8(int at, byte value) => bytes[at] = value;
        public void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        public void U64(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
        public void I64(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
        public void F32(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
    }
}
