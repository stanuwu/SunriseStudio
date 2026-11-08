using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

/** Generated structs read in place from a hand-built tag. */
static class StructChecks
{
    /** A scenario with two bubbles, laid out by hand, read through the generated structs. */
    public static void Run()
    {
        Check(Unsafe.SizeOf<SScenario>() == 136 && Unsafe.SizeOf<ScenarioBubble>() == 24, "A generated struct has the wrong size.");
        var bytes = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0xAABBCCDD);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(80), 2);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(88), 160 - 88);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(156), TagArray.Marker);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(160), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(168), ScenarioBubble.Class);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(176), 0x11111111);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(176 + 24), 0x22222222);

        ref readonly var scenario = ref TagView.At<SScenario>(bytes);
        Check(scenario.ActivityNameHash == 0xAABBCCDD && scenario.Bubbles.Count == 2, "A field read in place is wrong.");
        var bubbles = TagView.Rows(bytes, in scenario.Bubbles);
        Check(bubbles.Length == 2 && bubbles[0].NameHash == 0x11111111 && bubbles[1].NameHash == 0x22222222, "Typed rows were misread.");
        var copy = scenario;
        bool refused = false;
        try { _ = TagView.Rows(bytes, in copy.Bubbles); }
        catch (ArgumentException) { refused = true; }
        Check(refused, "A copied struct resolved an array it no longer knows the place of.");
        LoadAll();
    }

    /** Every generated struct must load and have its declared size; a layout cycle fails only at load. */
    private static void LoadAll()
    {
        var failed = new List<string>();
        int loaded = 0;
        Type[] types;
        try { types = typeof(SScenario).Assembly.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException error)
        {
            types = error.Types.OfType<Type>().ToArray();
            failed.AddRange(error.LoaderExceptions.OfType<Exception>().Select(x => x.Message).Distinct());
        }
        // The generated structs build into the Packages assembly with its other types.
        foreach (var type in types.Where(x => x.IsValueType && !x.IsGenericTypeDefinition && x.Namespace == typeof(SScenario).Namespace))
        {
            try
            {
                int size = RuntimeHelpers.SizeOf(type.TypeHandle);
                if (type.StructLayoutAttribute is { Value: System.Runtime.InteropServices.LayoutKind.Explicit, Size: > 0 } layout
                    && layout.Size != size)
                    failed.Add($"{type.Name}: size {size}, declared {layout.Size}");
                loaded++;
            }
            catch (TypeLoadException error) { failed.Add($"{type.Name}: {error.Message}"); }
        }
        Check(failed.Count == 0, $"{failed.Count} generated structs did not load: {string.Join("; ", failed.Take(5))}");
        Console.WriteLine($"  generated structs loaded={loaded}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
