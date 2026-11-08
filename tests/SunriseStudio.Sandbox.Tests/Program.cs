using System.Buffers.Binary;
using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

var graph = NativePerkBehavior.New();
graph.Activation.Add(NativePerkBehavior.CreateNode("timer"));
graph.Effects.Add(NativePerkBehavior.CreateNode("property"));
graph.Deactivation.Add(NativePerkBehavior.CreateNode("timer"));
Set(graph.Effects[0], "hash", 0x12345678);
Set(graph.Effects[0], "value", 1.75);
Set(graph.Effects[0], "operation", 3);
Set(graph.Activation[0], "seconds", 2.25);
Set(graph.Deactivation[0], "seconds", 4.5);
byte[] fresh = NativePerkBehavior.Compile(graph);
var decoded = NativePerkBehavior.Decompile(fresh);
Check(decoded.Activation.Count == 1 && decoded.Effects.Count == 1 && decoded.Deactivation.Count == 1,
    "Fresh phase arrays did not decode.");
Check(Value(decoded.Effects[0], "value") == 1.75 && Value(decoded.Deactivation[0], "seconds") == 4.5,
    "Fresh native values changed.");
Check(NativePerkBehavior.Compile(decoded).SequenceEqual(fresh), "Untouched native graph did not round trip.");
var reopened = JsonSerializer.Deserialize<PerkBehaviorGraph>(JsonSerializer.Serialize(decoded))!;
Check(NativePerkBehavior.Compile(reopened).SequenceEqual(fresh), "Graph serialization lost native bytes.");
Set(decoded.Activation[0], "seconds", 6);
byte[] patched = NativePerkBehavior.Compile(decoded);
int timerAt = decoded.Activation[0].OriginalOffset;
Check(patched.Length == fresh.Length && OnlyChanged(fresh, patched, timerAt + 8, 4),
    "Timer edit changed unrelated native fields.");
decoded.Activation.Add(NativePerkBehavior.CreateNode("always"));
byte[] extended = NativePerkBehavior.Compile(decoded);
var more = NativePerkBehavior.Decompile(extended);
Check(more.Activation.Count == 2 && more.Effects[0].OriginalOffset == decoded.Effects[0].OriginalOffset,
    "Structural edit moved an unrelated native node.");
var moreTimers = NativePerkBehavior.Decompile(fresh);
moreTimers.Activation.Add(NativePerkBehavior.CreateNode("timer"));
var timerResult = NativePerkBehavior.Decompile(NativePerkBehavior.Compile(moreTimers));
Check(timerResult.Activation.Concat(timerResult.Deactivation).Select(node => node.NativeBytes[7]).Distinct().Count() == 3,
    "Appended timer reused an existing native lane.");
byte[] hidden = (byte[])fresh.Clone();
I64(hidden, 88, BinaryPrimitives.ReadInt64LittleEndian(fresh.AsSpan(72)));
I64(hidden, 96, Target(fresh, 80) - 96);
var hiddenGraph = NativePerkBehavior.Decompile(hidden);
hiddenGraph.Activation.Add(NativePerkBehavior.CreateNode("timer"));
var cooldownResult = NativePerkBehavior.Decompile(NativePerkBehavior.Compile(hiddenGraph));
Check(cooldownResult.Activation.Concat(cooldownResult.Deactivation).Select(node => node.NativeBytes[7]).Distinct().Count() == 3,
    "An appended timer reused a cooldown condition's index.");
hidden[104] = 1;
var stackGraph = NativePerkBehavior.Decompile(hidden);
stackGraph.Activation.Add(NativePerkBehavior.CreateNode("timer"));
Reject(() => NativePerkBehavior.Compile(stackGraph));
var wrongKind = NativePerkBehavior.Decompile(fresh);
var movedCondition = wrongKind.Deactivation[0];
wrongKind.Deactivation.Clear();
movedCondition.Kind = PerkBehaviorNodeKind.Effect;
wrongKind.Effects.Add(movedCondition);
Reject(() => NativePerkBehavior.Compile(wrongKind));
var sharedGraph = NativePerkBehavior.Decompile(fresh);
var secondProperty = NativePerkBehavior.CreateNode("property");
Set(secondProperty, "hash", 0x12345679); Set(secondProperty, "value", 1.75);
sharedGraph.Effects.Add(secondProperty);
byte[] sharedBytes = NativePerkBehavior.Compile(sharedGraph);
var sharedNodes = NativePerkBehavior.Decompile(sharedBytes);
int firstConstantHeader = Target(sharedBytes, sharedNodes.Effects[0].OriginalOffset + 48);
int secondConstantPointer = sharedNodes.Effects[1].OriginalOffset + 48;
I64(sharedBytes, secondConstantPointer, firstConstantHeader - secondConstantPointer);
var sharedEdit = NativePerkBehavior.Decompile(sharedBytes);
Check(NativePerkBehavior.Compile(sharedEdit).SequenceEqual(sharedBytes), "A shared constant changed without an edit.");
Set(sharedEdit.Effects[0], "value", 2.5);
byte[] changedConstant = NativePerkBehavior.Compile(sharedEdit);
var changedNodes = NativePerkBehavior.Decompile(changedConstant);
Check(Value(changedNodes.Effects[0], "value") == 2.5 && Value(changedNodes.Effects[1], "value") == 1.75
    && sharedBytes.AsSpan(firstConstantHeader + 16, 16).SequenceEqual(changedConstant.AsSpan(firstConstantHeader + 16, 16)),
    "Editing a scalar changed another node's shared constant.");
Set(graph.Effects[0], "hash", 0);
Reject(() => NativePerkBehavior.Compile(graph));
Set(graph.Effects[0], "hash", 0x12345678);
Set(graph.Activation[0], "seconds", double.NaN);
Reject(() => NativePerkBehavior.Compile(graph));
var ammo = NativePerkBehavior.New();
ammo.Activation.Add(NativePerkBehavior.CreateNode("timer"));
ammo.Effects.Add(NativePerkBehavior.CreateNode("ammo-drop"));
ammo.Deactivation.Add(NativePerkBehavior.CreateNode("timer"));
Set(ammo.Effects[0], "special_chance", 0.4);
Set(ammo.Effects[0], "heavy_interval", 0.2);
var ammoDecoded = NativePerkBehavior.Decompile(NativePerkBehavior.Compile(ammo));
Check(ammoDecoded.Effects[0].Primitive == "ammo-drop"
    && Math.Abs(Value(ammoDecoded.Effects[0], "special_chance") - 0.4) < 0.000001,
    "Scratch ammo modifier did not decode its native values.");
var reserve = NativePerkBehavior.New();
reserve.Activation.Add(NativePerkBehavior.CreateNode("always"));
reserve.Effects.Add(NativePerkBehavior.CreateNode("ammo-drop"));
Set(reserve.Effects[0], "primary_chance", 0.10);
byte[] reserveBytes = NativePerkBehavior.Compile(reserve);
var reserveDecoded = NativePerkBehavior.Decompile(reserveBytes);
Check(reserve.OriginalBytes.Length == 0 && reserve.Activation.All(node => node.OriginalOffset < 0)
    && reserve.Effects.All(node => node.OriginalOffset < 0) && reserveDecoded.Activation.Single().Primitive == "always"
    && reserveDecoded.Deactivation.Count == 0 && BinaryPrimitives.ReadUInt64LittleEndian(reserveBytes.AsSpan(136)) == 1
    && BinaryPrimitives.ReadUInt64LittleEndian(reserveBytes.AsSpan(144)) == 0
    && Math.Abs(Value(reserveDecoded.Effects.Single(), "primary_chance") - 0.10) < 0.000001
    && NativePerkBehavior.Compile(reserveDecoded).SequenceEqual(reserveBytes),
    "The fresh persistent catalyst perk lost its native activation or effect fields.");
Registration();
Console.WriteLine("sandbox scratch=pass roundtrip=pass patch=pass relocation=pass validation=pass registration=pass");
PerkKindChecks.Run();

// --perk-dir reads dumped programs named <tag>.bin; --perk-census reads every installed program.
int perkDir = Array.IndexOf(args, "--perk-dir");
if (perkDir >= 0 && perkDir + 1 < args.Length)
{
    var dumped = Directory.GetFiles(args[perkDir + 1], "*.bin").Order(StringComparer.Ordinal)
        .Select(path => (Convert.ToUInt32(Path.GetFileName(path)[..8], 16), File.ReadAllBytes(path))).ToArray();
    PerkKindChecks.Corpus(dumped);
    // --labels names a dump of the label table tag 0x80C70CA1.
    int labels = Array.IndexOf(args, "--labels");
    if (labels >= 0 && labels + 1 < args.Length && PerkLabelTable.Parse(File.ReadAllBytes(args[labels + 1])) is { } table)
        PerkKindChecks.LabelCorpus(dumped, table);
}

int game = Array.IndexOf(args, "--game-root");
if (game >= 0 && game + 1 < args.Length)
{
    using var catalog = PackageCatalog.Open(args[game + 1], cacheOptions: new() { Enabled = false });
    foreach (int index in new[] { 351, 368, 421, 450, 456, 1873, 1874, 1879 })
    {
        var perk = InvestmentSandboxPerkAuthoring.Read(catalog, index);
        var original = NativePerkBehavior.Decompile(perk.Gameplay);
        Check(NativePerkBehavior.Compile(original).SequenceEqual(perk.Gameplay),
            $"Installed perk {index} did not preserve its bytes.");
        Console.WriteLine($"sandbox_owned index={index} tag={perk.GameplayTag:X8} roundtrip=pass nodes={original.Activation.Count + original.Effects.Count + original.Deactivation.Count}");
    }
    Reject(() => InvestmentSandboxPerkAuthoring.Read(catalog, 1878));
    var inserted = InvestmentSandboxPerkAuthoring.Register(catalog.Read(InvestmentSandboxPerkAuthoring.DefinitionTableTag),
        catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag), catalog.Read(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag),
        0xABCDEF01, 0xABCDEF02, 0x81FFE000, 0, 0xABCDEF03, 0xABCDEF04, SandboxPerkPresentation.Normal,
        catalog.ReadOriginal(InvestmentSandboxPerkAuthoring.DisplayTableTag).Length);
    Check(inserted.Index == 2481 && InvestmentSandboxPerkAuthoring.ResolveRuntimeName(inserted.RuntimeNames, 0xABCDEF02) == 0x81FFE000,
        "Installed sandbox registration failed.");
    Console.WriteLine("sandbox_owned registration=pass inactive=reject source=unchanged");
    if (Array.IndexOf(args, "--perk-census") >= 0)
    {
        var installed = catalog.Entries.Where(entry => entry.Reference == NativePerkBehavior.Class && entry.Type is 8 or 16)
            .Select(entry => entry.TagHash).Distinct().Order().Select(tag => (tag, catalog.Read(tag))).ToArray();
        PerkKindChecks.Corpus(installed);
        PerkKindChecks.LabelCorpus(installed, PerkLabelTable.Parse(catalog.Read(PerkLabelTable.Tag))
            ?? throw new InvalidDataException("The installed label table did not parse."));
    }
    if (Array.IndexOf(args, "--all-perks") >= 0)
    {
        byte[] table = catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag);
        int count = checked((int)BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(8)));
        int rows = Target(table, 16) + 16, passed = 0, skipped = 0;
        var failures = new List<string>();
        for (int index = 0; index < count; index++)
        {
            uint name = U32Read(table, rows + index * 24 + 4);
            if (name == StringHash.Empty) { skipped++; continue; }
            uint tag = InvestmentSandboxPerkAuthoring.ResolveRuntimeName(
                catalog.Read(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag), name);
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != NativePerkBehavior.Class)
            { skipped++; continue; }
            byte[] bytes = catalog.Read(tag);
            try
            {
                Check(NativePerkBehavior.Compile(NativePerkBehavior.Decompile(bytes)).SequenceEqual(bytes), "Round trip differs.");
                passed++;
            }
            catch (Exception error) { failures.Add($"{index}:{tag:X8}:{error.Message}"); }
        }
        Console.WriteLine($"sandbox_corpus total={count} mapped={passed + failures.Count} roundtrip={passed} no_program={skipped} failures={failures.Count}");
        foreach (string failure in failures.Take(20)) Console.WriteLine(failure);
        Check(failures.Count == 0, "Some installed perk programs did not round trip.");
    }
}

/** The fixture forces both display arrays to move while the original text target remains shared. */
static void Registration()
{
    byte[] definitions = new byte[72];
    Typed(definitions, 8, 32, 2, InvestmentSandboxPerkAuthoring.DefinitionRowClass);
    U32(definitions, 48, 11); U32(definitions, 56, 12);
    definitions[52] = definitions[53] = definitions[60] = definitions[61] = 255;
    definitions[54] = definitions[62] = 1;
    byte[] displays = new byte[164];
    Typed(displays, 8, 48, 2, 0x80805C9D);
    Typed(displays, 24, 120, 1, 0x80805C9F);
    U32(displays, 64, 11); U32(displays, 68, 21); I64(displays, 72, 136 - 72);
    U32(displays, 88, 12); U32(displays, 92, 22); I64(displays, 96, 136 - 96);
    U32(displays, 136, 0); U32(displays, 140, 123); displays[160] = 0xAD;
    byte[] names = new byte[80];
    Typed(names, 8, 32, 2, InvestmentSandboxPerkAuthoring.RuntimeNameRowClass);
    U32(names, 48, 21); U32(names, 52, 0x80810000); U32(names, 56, 22); U32(names, 60, 0x80810001);
    names[79] = 0xBC;
    foreach (byte[] bytes in new[] { definitions, displays, names }) I64(bytes, 0, bytes.Length);
    var added = InvestmentSandboxPerkAuthoring.Register(definitions, displays, names,
        13, 20, 0x81FFE000, 7, 1001, 1002, SandboxPerkPresentation.Normal, displays.Length, 3);
    Check(added.Index == 2 && InvestmentSandboxPerkAuthoring.ResolveRuntimeName(added.RuntimeNames, 20) == 0x81FFE000,
        "New runtime name did not join the map.");
    int rows = Target(added.Displays, 16) + 16;
    Check(Target(added.Displays, rows + 8) == 136 && Target(added.Displays, rows + 24 + 8) == 136,
        "Shared original display pointers moved.");
    int text = Target(added.Displays, rows + 48 + 8);
    Check(U32Read(added.Displays, text) == 7 && U32Read(added.Displays, text + 4) == 1001
        && added.Displays[160] == 0xAD && added.RuntimeNames[79] == 0xBC,
        "Registration changed unrelated display or map bytes.");
    Check(BinaryPrimitives.ReadUInt16LittleEndian(added.Displays.AsSpan(text + 16)) == 3
        && BinaryPrimitives.ReadUInt16LittleEndian(added.Displays.AsSpan(text + 18)) == 0
        && U32Read(added.Displays, text + 20) == StringHash.Empty
        && U32Read(added.Displays, text + 24) == StringHash.Empty,
        "Fresh display fields do not match the native default.");
    var second = InvestmentSandboxPerkAuthoring.Register(added.Definitions, added.Displays, added.RuntimeNames,
        14, 23, 0x81FFE001, 8, 1003, 1004, SandboxPerkPresentation.Normal, displays.Length, 4);
    int secondRows = Target(second.Displays, 16) + 16;
    int secondTexts = Target(second.Displays, 32) + 16;
    Check(Target(second.Displays, secondRows + 8) == secondTexts
        && Target(second.Displays, secondRows + 32) == secondTexts
        && Target(second.Displays, secondRows + 56) == secondTexts + 28
        && Target(second.Displays, secondRows + 80) == secondTexts + 56,
        "A second registration did not rebase shared display text pointers.");
    var defaults = InvestmentSandboxPerkAuthoring.Register(second.Definitions, second.Displays,
        second.RuntimeNames, 15, 24, 0x81FFE002, 9, StringHash.Empty,
        StringHash.Empty, SandboxPerkPresentation.Normal, displays.Length);
    int defaultRows = Target(defaults.Displays, 16) + 16;
    int defaultText = Target(defaults.Displays, defaultRows + defaults.Index * 24 + 8);
    byte[] nativeDefault = Convert.FromHexString(
        "FFFF0000C59D1C81FFFF0000C59D1C81FFFF0000C59D1C81C59D1C81");
    Check(defaults.Displays.AsSpan(defaultText, 28).SequenceEqual(nativeDefault),
        "An unassigned display does not match all 28 native default bytes.");
    var exotic = InvestmentSandboxPerkAuthoring.Register(definitions, displays, names,
        13, 20, 0x81FFE000, 7, 1001, 1002, SandboxPerkPresentation.Exotic, displays.Length, 3);
    Check(InvestmentSandboxPerkAuthoring.ReadPresentation(exotic.Displays, 2) == SandboxPerkPresentation.Exotic
        && OnlyChanged(added.Displays, exotic.Displays, text + 20, 4)
        && exotic.Definitions.SequenceEqual(added.Definitions)
        && exotic.RuntimeNames.SequenceEqual(added.RuntimeNames),
        "Exotic presentation changed gameplay identity or unrelated display fields.");
    byte[] normal = InvestmentSandboxPerkAuthoring.SetPresentation(exotic.Displays, 2, 13,
        SandboxPerkPresentation.Normal);
    Check(normal.SequenceEqual(added.Displays), "Restoring normal presentation changed unrelated bytes.");
    byte[] icon = InvestmentSandboxPerkAuthoring.SetIcon(exotic.Displays, 2, 13, 71);
    Check(OnlyChanged(exotic.Displays, icon, text + 16, 2)
        && BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(text + 16)) == 71,
        "A perk icon edit changed unrelated display fields.");
    Reject(() => InvestmentSandboxPerkAuthoring.SetIcon(exotic.Displays, 2, 99, 71));
    Reject(() => InvestmentSandboxPerkAuthoring.SetIcon(exotic.Displays, 0, 11, 71));
    Reject(() => InvestmentSandboxPerkAuthoring.SetPresentation(exotic.Displays, 2, 99,
        SandboxPerkPresentation.Normal));
    Reject(() => InvestmentSandboxPerkAuthoring.SetPresentation(exotic.Displays, 0, 11,
        SandboxPerkPresentation.Exotic));
    Reject(() => InvestmentSandboxPerkAuthoring.Register(definitions, displays, names,
        11, 20, 0x81FFE000, 7, 1001, 1002, SandboxPerkPresentation.Normal, displays.Length));
    Reject(() => InvestmentSandboxPerkAuthoring.AddRuntimeName(names, 21, 0x81FFE000));
}

static void Set(PerkBehaviorNode node, string key, double value) => node.Properties.Single(row => row.Key == key).Value = value;
static double Value(PerkBehaviorNode node, string key) => node.Properties.Single(row => row.Key == key).Value;
static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static void Reject(Action action)
{ try { action(); } catch (InvalidDataException) { return; } throw new InvalidDataException("Invalid authoring input was accepted."); }
static bool OnlyChanged(byte[] before, byte[] after, int at, int size) => before.Length == after.Length
    && Enumerable.Range(0, before.Length).All(i => i >= at && i < at + size || before[i] == after[i]);
static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
static uint U32Read(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
static void I64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
static int Target(byte[] bytes, int at) => checked(at + (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at)));
static void Typed(byte[] bytes, int field, int header, int count, uint type)
{
    I64(bytes, field, count); I64(bytes, field + 8, header - field - 8);
    U32(bytes, header - 4, 0x80809FBD); I64(bytes, header, count); U32(bytes, header + 8, type);
}
