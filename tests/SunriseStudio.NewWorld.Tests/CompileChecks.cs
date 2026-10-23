using SunriseStudio.Authoring;
using SunriseStudio.Formats.NewWorld;
using SunriseStudio.Packages;
using static Expect;

/** Compiles one and two bubbles and checks allocation, metadata, determinism and refusals. */
internal static class CompileChecks
{
    internal const ushort Package = 0x0AA0;

    internal static NewWorldRecipe One => NewWorldRecipe.Create("studio_new_world", "New World", 1, 32, "flat-grass");

    internal static NewWorldRecipe Two => new("studio_two_world", "Two Bubbles",
    [
        new("bubble_00", "First", [0, 0, 0], 32, 32, "flat-grass", [0, 0, 0.15]),
        new("bubble_01", "Second", [64, 0, 0], 16, 24, "flat-stone", [64, 0, 0.15])
    ]);

    internal static IReadOnlyList<NewWorldBuild> Run(PackageCatalog catalog)
    {
        var installed = NewWorldCompiler.ReadInstalled(catalog);
        Check(installed.CollisionArchetypeClosure.SequenceEqual(NewWorldFixture.CollisionClosure)
            && installed.SurfaceClosure.SequenceEqual(NewWorldFixture.SurfaceClosure)
            && installed.NavigationEntityClosure.SequenceEqual(NewWorldFixture.EntityClosure)
            && installed.NavigationRootClosure.SequenceEqual(NewWorldFixture.RootClosure), "The retail closure scan differs.");
        var one = NewWorldCompiler.Compile(catalog, One, Package, 0);
        var two = NewWorldCompiler.Compile(catalog, Two, Package, 3);
        foreach (var (build, recipe, first) in new[] { (one, One, 0), (two, Two, 3) })
        {
            var entries = build.Allocation.Entries;
            Check(build.Additions.Count == entries.Count + 1 && build.Additions[0].Native is null
                && build.Additions[0].Name == recipe.Name + ".newmap.json", "The recipe asset does not lead the additions.");
            Check(NewWorldRecipe.FromJson(build.Additions[0].Data) is { } read && read.Name == recipe.Name
                && read.Bubbles.Count == recipe.Bubbles.Count, "The recipe asset does not read back.");
            Check(build.Additions.All(asset => asset.Group == recipe.Name), "An addition left the map group.");
            Check(entries.Select((entry, i) => entry.EntryIndex == first + i && entry.Tag == GeneratedGraph.TagOf(Package, first + i)).All(x => x),
                "Entries are not contiguous from the first index.");
            Check(entries.Select(entry => entry.Key).SequenceEqual(entries.Select(entry => entry.Key).Order(StringComparer.Ordinal)),
                "Entries are not in ordinal key order.");
            foreach (var entry in entries.Where(entry => entry.TypeInfo >> 9 == 16))
                Check(build.Allocation.Companions.TryGetValue(entry.Key, out var companion) && build.Allocation.Tags.ContainsKey(companion), $"{entry.Key} has no root manifest.");
            Check(build.MapTag == build.Allocation.Tags[NewWorldGraph.WorldKey], "The map tag is not the world root.");
            var named = build.Additions.Where(asset => asset.Native?.Name is not null).ToDictionary(asset => asset.Native!.Name!);
            Check(named.Count == 3 && named.ContainsKey(recipe.MapName) && named.ContainsKey(recipe.Name)
                && named.ContainsKey(recipe.Name + ":scenario_client"), "Root, activity or scenario name is missing.");
            Check(build.Additions.Count(asset => asset.BaseTagHash is not null) == 7 * recipe.Bubbles.Count + 3,
                "Derived profile copies are not stored against their originals.");
        }
        int bubbles = one.Allocation.Entries.Count(entry => entry.Key.StartsWith("b0_", StringComparison.Ordinal));
        Check(two.Allocation.Entries.Count(entry => entry.Key.StartsWith("b1_", StringComparison.Ordinal)) == bubbles,
            "Each bubble does not compile the same resource set.");
        var again = NewWorldCompiler.Compile(catalog, NewWorldRecipe.FromJson(Two.ToJson()), Package, 3);
        Check(again.Additions.Zip(two.Additions).All(pair => pair.First.Name == pair.Second.Name
            && pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data) && pair.First.Native == pair.Second.Native),
            "The same recipe gave different bytes.");
        var wide = NewWorldCompiler.Compile(catalog, NewWorldRecipe.Create("wide_world", "Wide", 64, 0.5, "flat-sand"), Package, 0);
        Check(wide.Allocation.Entries.Count < 8192 && wide.Allocation.Entries.Count(entry => entry.Key.EndsWith("_bubble", StringComparison.Ordinal)) == 64,
            "Sixty-four bubbles did not compile.");
        Throws<InvalidDataException>(() => NewWorldRecipe.Create("Bad", "Title", 1, 32, "flat-grass"), "An upper-case name was accepted.");
        Throws<InvalidDataException>(() => NewWorldRecipe.Create("zero", "Title", 0, 32, "flat-grass"), "Zero bubbles were accepted.");
        Throws<InvalidDataException>(() => NewWorldRecipe.Create("many", "Title", 65, 32, "flat-grass"), "65 bubbles were accepted.");
        Throws<InvalidDataException>(() => NewWorldRecipe.Create("odd", "Title", 1, 0.01, "flat-grass"), "A size off the 1/64 grid was accepted.");
        Throws<InvalidDataException>(() => NewWorldRecipe.Create("hue", "Title", 1, 32, "flat-blue"), "An unknown colour was accepted.");
        Throws<InvalidDataException>(() => NewWorldRecipe.FromJson("{\"name\":\"x\",\"title\":\"x\",\"bubbles\":[],\"extra\":1}"u8),
            "An unknown recipe field was accepted.");
        Throws<InvalidDataException>(() => NewWorldCompiler.Compile(catalog, One, 0x0238, 0), "An installed package id was accepted.");
        Throws<InvalidDataException>(() => NewWorldCompiler.Compile(catalog, Two, Package, 8100), "A map past the package entry limit was accepted.");
        Console.WriteLine($"PASS new-world-compile one={one.Allocation.Entries.Count} two={two.Allocation.Entries.Count} "
            + $"sixty-four={wide.Allocation.Entries.Count} per-bubble={bubbles} deterministic=yes refusals=8");
        return [one, two];
    }
}
