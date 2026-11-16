using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Installed emitter programs run without a stage fault, and spawn shapes follow the native samplers. */
internal static class EmitterChecks
{
    /** @param all Runs every installed emitter instead of every eighth one. */
    public static void Run(string gameRoot, bool all)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var reader = new EffectReader(catalog);
        var tags = catalog.Entries.Where(e => e.Reference == EffectReader.EmitterClass).Select(e => e.TagHash)
            .Distinct().Order().Where((_, i) => all || i % 8 == 0).ToArray();
        int ran = 0, emitting = 0, stages = 0, instructions = 0;
        var failures = new Dictionary<string, (int Count, uint Example)>();
        foreach (uint tag in tags)
        {
            if (reader.ReadEmitter(tag) is not { } emitter) continue;
            // Every stage splits into instructions and joins again to the same bytes.
            int start = 0;
            foreach (ushort length in emitter.StageLengths)
            {
                var stage = emitter.Program.AsSpan(start, length);
                var parts = SunriseStudio.Rendering.EmitterCompiler.Disassemble(stage);
                Require(SunriseStudio.Rendering.EmitterCompiler.Assemble(parts).AsSpan().SequenceEqual(stage),
                    $"Emitter 0x{tag:X8} stage at {start} does not reassemble to its bytes.");
                start += length; stages++; instructions += parts.Count;
            }
            var simulation = new EmitterSimulation(emitter, Matrix4x4.Identity, [], 5, EffectSimulation.EmitterCap, tag);
            simulation.Advance(0, 10);
            simulation.Advance(0.5, 10);
            ran++;
            if (simulation.Count > 0) emitting++;
            if (simulation.Failure is { } failure)
                failures[failure] = (failures.GetValueOrDefault(failure).Count + 1, failures.GetValueOrDefault(failure).Example is 0 ? tag : failures[failure].Example);
        }
        Require(failures.Count == 0, "Emitter stages failed: " + string.Join("; ",
            failures.Select(p => $"{p.Key} x{p.Value.Count} (0x{p.Value.Example:X8})")));
        Require(emitting > ran / 4, $"Only {emitting} of {ran} emitters have live particles after 2.5 seconds.");
        Console.WriteLine($"emitter_programs ran={ran} emitting={emitting} failed=0 scope={(all ? "all" : "eighth")}");
        Console.WriteLine($"emitter_reassembly stages={stages} instructions={instructions} result=identical");
        Ring(reader);
        Authored();
        Written(catalog);
    }

    /** A program written into a copy of the ring emitter reads back; the unchanged program gives the same bytes. */
    private static void Written(PackageCatalog catalog)
    {
        const uint tag = 0x80B38183;
        var schema = SunriseStudio.Packages.Registry.StudioRegistry.Index;
        byte[] installed = catalog.Read(tag);
        var held = SunriseStudio.Interchange.EmitterWriter.Read(installed);
        Require(SunriseStudio.Interchange.EmitterWriter.WithProgram(installed, schema, held).AsSpan().SequenceEqual(installed),
            "Writing an emitter's own program back changed its bytes.");
        // A longer program with more constants: the update stage gets a gradient of the particle's age.
        var builder = new SunriseStudio.Rendering.EmitterBuilder();
        for (int stage = 0; stage < SunriseStudio.Interchange.EmitterWriter.StageCount; stage++)
        {
            if (stage == 4) builder.Load(1, 0, SunriseStudio.Rendering.EmitterLanes.X)
                .Gradient([(0, new(1, 1, 1, 1)), (0.5f, new(1, 0.5f, 0, 1)), (1, new(0, 0, 0, 0))]).Store(1, 3);
            builder.EndStage();
        }
        var code = builder.Build();
        byte[] written = SunriseStudio.Interchange.EmitterWriter.WithProgram(installed, schema, code);
        var back = SunriseStudio.Interchange.EmitterWriter.Read(written);
        Require(back.Program.AsSpan().SequenceEqual(code.Program) && back.StageLengths.AsSpan().SequenceEqual(code.StageLengths)
            && back.Constants.AsSpan().SequenceEqual(code.Constants), "A written emitter program does not read back.");
        bool refused = false;
        try { SunriseStudio.Interchange.EmitterWriter.WithProgram(installed, schema, code with { StageLengths = [1, 2] }); }
        catch (ArgumentException) { refused = true; }
        Require(refused, "A program without eight stage lengths was written.");
        // Channel 13 is the ring radius, held as one constant float; channel 0 is not a constant.
        byte[] wider = SunriseStudio.Interchange.EmitterWriter.WithChannel(installed, 13, 2f);
        var changed = Enumerable.Range(0, installed.Length).Where(at => wider[at] != installed[at]).ToArray();
        Require(wider.Length == installed.Length && changed.Length > 0 && changed[^1] - changed[0] < sizeof(float)
            && BitConverter.ToSingle(wider, changed[0] / sizeof(float) * sizeof(float)) == 2f, "Setting the radius channel changed other bytes.");
        refused = false;
        try { SunriseStudio.Interchange.EmitterWriter.WithChannel(installed, 0, 1f); }
        catch (InvalidDataException) { refused = true; }
        Require(refused, "A channel that is not a constant was written.");
        Console.WriteLine($"emitter_writer tag={tag:X8} own_program=identical new_bytes={code.Program.Length} new_constants={code.Constants.Length} size={installed.Length}->{written.Length} wrong_stages=refused channel=written");
    }

    /** A built program: a random start value in one stage, a gradient of it in the next. */
    private static void Authored()
    {
        Vector4 low = new(1, 2, 3, 4), high = new(5, 6, 7, 8);
        (float, Vector4)[] keys = [(0, new(0, 0, 0, 1)), (0.25f, new(1, 0, 0, 1)), (1, new(1, 1, 0, 0))];
        var code = new SunriseStudio.Rendering.EmitterBuilder()
            .RandomBetween(low, high).Store(1, 0).EndStage()
            .Load(1, 1, SunriseStudio.Rendering.EmitterLanes.X).Gradient(keys).Store(2, 3).EndStage()
            .Build();
        Require(code.StageLengths.Length == 2 && code.StageLengths.Sum(length => length) == code.Program.Length && code.Constants.Length == 8,
            "The built program has other stages or constants than it was given.");
        Vector4 Sample(float x)
        {
            var context = new SunriseStudio.Rendering.EmitterProgramContext { Constants = code.Constants, Random = 12345 };
            context.Groups[1] = new Vector4[2]; context.Groups[2] = new Vector4[4];
            context.Groups[1]![1] = new Vector4(x);
            var twin = new SunriseStudio.Rendering.EmitterProgramContext { Random = 12345 };
            float random = twin.NextRandom();
            SunriseStudio.Rendering.EmitterProgram.Run(code.Program.AsSpan(0, code.StageLengths[0]), context);
            Require((context.Groups[1]![0] - Vector4.Lerp(low, high, random)).Length() < 1e-4f, "The random start value is not between its constants.");
            SunriseStudio.Rendering.EmitterProgram.Run(code.Program.AsSpan(code.StageLengths[0]), context);
            return context.Groups[2]![3];
        }
        (float X, Vector4 Expected)[] points = [(0, keys[0].Item2), (0.125f, new(0.5f, 0, 0, 1)), (0.25f, keys[1].Item2), (0.625f, new(1, 0.5f, 0, 0.5f)), (1, keys[2].Item2)];
        foreach (var (x, expected) in points)
            Require((Sample(x) - expected).Length() < 1e-4f, $"The built gradient gives {Sample(x)} at {x}, not {expected}.");
        bool refused = false;
        try { new SunriseStudio.Rendering.EmitterBuilder().Constant(1).EndStage(); }
        catch (InvalidDataException) { refused = true; }
        Require(refused, "A stage that leaves a value on the stack was accepted.");
        // A curve passes through its keys, holds its last value and has no step at a key.
        (float Position, float Value)[] curve = [(0, 0), (0.2f, 1), (0.6f, 0.4f), (1, 0)];
        var curved = new SunriseStudio.Rendering.EmitterBuilder().Load(1, 0, SunriseStudio.Rendering.EmitterLanes.X).Curve(curve).Store(1, 1).EndStage().Build();
        float Curved(float x)
        {
            var context = new SunriseStudio.Rendering.EmitterProgramContext { Constants = curved.Constants };
            context.Groups[1] = new Vector4[2];
            context.Groups[1]![0] = new Vector4(x);
            SunriseStudio.Rendering.EmitterProgram.Run(curved.Program, context);
            return context.Groups[1]![1].X;
        }
        foreach (var (position, value) in curve)
            Require(MathF.Abs(Curved(position) - value) < 1e-4f && MathF.Abs(Curved(MathF.Max(position - 1e-4f, 0)) - value) < 2e-3f,
                $"The built curve gives {Curved(position)} at {position}, not {value}.");
        Require(MathF.Abs(Curved(1.5f)) < 1e-4f && Curved(0.1f) is > 0.3f and < 1 && Curved(0.4f) is > 0.4f and < 1.1f,
            "The built curve leaves its keys between them.");
        Console.WriteLine($"emitter_builder stages=2 bytes={code.Program.Length} constants={code.Constants.Length} gradient_points={points.Length} curve_keys={curve.Length} unbalanced_stage=refused");
    }

    /** Emitter 0x80B38183 spawns on a radius-1 ring with no speed; its spawn stage copies that point unchanged. */
    private static void Ring(EffectReader reader)
    {
        const uint tag = 0x80B38183;
        var emitter = reader.ReadEmitter(tag) ?? throw new InvalidOperationException("Ring fixture emitter is missing.");
        Require(emitter is { Shape: 1, Radius: 1, InitialSpeed: 0 } && emitter.ShapeScale == Vector3.One,
            "Ring fixture emitter changed its authored shape.");
        // Each seed spawns its own point; together they must cover the ring.
        var angles = new List<float>();
        for (uint seed = 1; seed <= 16; seed++)
        {
            var simulation = new EmitterSimulation(emitter, Matrix4x4.Identity, [], 5, EffectSimulation.EmitterCap, seed);
            simulation.Advance(0, 10);
            Require(simulation.Count > 0, $"Ring fixture emitter has no live particles for seed {seed}.");
            for (int i = 0; i < simulation.Count; i++)
            {
                var position = simulation.ParticleAt(i).Position;
                float ring = MathF.Sqrt(position.Y * position.Y + position.Z * position.Z);
                Require(MathF.Abs(position.X) < 1e-3f && MathF.Abs(ring - 1) < 1e-3f,
                    $"Emitter 0x{tag:X8} placed {position} off its unit ring.");
                angles.Add(MathF.Atan2(position.Y, position.Z));
            }
        }
        float spread = angles.Max() - angles.Min();
        Require(spread > MathF.PI, $"Ring spawns cover only {spread:0.##} radians.");
        Console.WriteLine($"emitter_ring tag={tag:X8} radius=1 points={angles.Count} spread={spread:0.##} result=pass");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
