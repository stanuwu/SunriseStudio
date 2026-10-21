using System.Diagnostics;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Scene.Collision;
using static Checks;
using static StaticPlacementFixture;

/**
 * Runs World Studio's compilers on the same placements and compares the bytes. The driver and World Studio
 * live outside this repository, so the check runs only when they are named on the command line.
 */
internal static class ParityChecks
{
    internal static void Run(PackageCatalog catalog, PlacedFixture placed, string python, string worldStudio, string driver, string output)
    {
        Directory.CreateDirectory(output);
        var generated = placed.Compiled.GeneratedAssets.ToDictionary(asset => asset.Native.Reference);
        var table = generated[0x8080966D];
        var before = StaticCompoundShape.Parse(HavokPackfile.Parse(catalog.ReadOriginal(Compound)));
        var after = StaticCompoundShape.Parse(HavokPackfile.Parse(placed.Compiled.Assets[Compound]));
        string Instance(StaticCompoundShape shape, int index) => Convert.ToHexString(shape.File.Bytes.AsSpan(
            shape.File.Absolute(shape.Instances[index].Offset), StaticCompoundShape.InstanceSize));
        JsonArray Bits(float[] values) => new(values.Select(value => (JsonNode)BitConverter.SingleToUInt32Bits(value)).ToArray());
        var input = new JsonObject
        {
            ["models"] = new JsonObject
            {
                [$"{ModelA:X8}"] = new JsonObject { ["hex"] = Convert.ToHexString(catalog.ReadOriginal(ModelA)), ["vertices"] = ModelAVertices },
                [$"{ModelB:X8}"] = new JsonObject { ["hex"] = Convert.ToHexString(catalog.ReadOriginal(ModelB)), ["vertices"] = ModelBVertices }
            },
            ["rows"] = new JsonArray(placed.Edits.Select(edit => (JsonNode)new JsonObject
            {
                ["mesh_tag"] = $"{(edit.Target.Index == 0 ? ModelA : ModelB):X8}",
                ["position"] = Bits(edit.Position!),
                ["rotation"] = Bits(edit.Rotation!),
                ["scale"] = BitConverter.SingleToUInt32Bits(edit.Scale!.Value)
            }).ToArray()),
            ["instance_auxiliary"] = InstanceAuxiliary,
            ["parent_auxiliary"] = OwnerAuxiliary,
            ["table"] = Convert.ToHexString(table.Data),
            ["table_tag"] = table.TagHash,
            ["bounds"] = Convert.ToHexString(generated[0x80809671].Data),
            ["bounds_tag"] = generated[0x80809671].TagHash,
            ["owner"] = Convert.ToHexString(generated[0x80806EF4].Data),
            ["owner_tag"] = generated[0x80806EF4].TagHash,
            ["list"] = Convert.ToHexString(generated[0x808099D6].Data),
            ["template_list"] = Convert.ToHexString(catalog.ReadOriginal(List)),
            ["template_row"] = 0,
            ["compound"] = new JsonArray(placed.Edits.Select((edit, i) => (JsonNode)new JsonObject
            {
                ["source"] = Instance(before, edit.Target.Index),
                ["output"] = Instance(after, before.Instances.Count + i)
            }).ToArray())
        };
        string inputPath = Path.Combine(output, "parity-input.json"), resultPath = Path.Combine(output, "parity-result.json");
        File.WriteAllText(inputPath, input.ToJsonString(new() { WriteIndented = true }));
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-I", driver, worldStudio, inputPath, resultPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Check(process.ExitCode == 0, $"World Studio parity failed: {stdout.Result}{stderr.Result}");
        Console.WriteLine($"PASS placement-parity world-studio {stdout.Result.Trim()} result={resultPath}");
    }
}
