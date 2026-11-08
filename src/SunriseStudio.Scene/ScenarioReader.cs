using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Scene;

public sealed partial class AssetSceneLoader
{
    private SceneLoadOptions options = new();
    private uint activeEntryIndex = uint.MaxValue;
    private readonly Dictionary<AuthoredSlotIdentity, uint> slotNames = [];

    /** Localized and inline names; null keeps the package and hash-candidate names only. */
    public GameNames? GameNames { get; set; }

    /** Dev name, location and destination text of a scenario. */
    public ScenarioNames DescribeScenarioNames(uint tag) =>
        GameNames?.Scenario(tag) ?? GameNames.ReadScenario(catalog, tag, null);

    /** Localized bubble text first, with the authored name beside it. */
    private string BubbleDisplayName(uint hash)
    {
        var authored = ResolveHashName(hash).Name;
        return GameNames?.BubbleTitle(hash, authored) ?? authored;
    }

    /** Describes authored states without selecting runtime activity state. */
    public ScenarioInfo DescribeScenario(uint tag)
    {
        if (Class(tag) != SScenario.Class) throw new InvalidDataException("Tag is not a scenario.");
        var data = catalog.Read(tag);
        CollectInlineNames(tag, data);
        ref readonly var scenario = ref TagView.Checked<SScenario>(data, 0);
        var bubbles = new List<ScenarioBubbleInfo>();
        foreach (ref readonly var bubble in TagView.Rows(data, in scenario.Bubbles, ScenarioBubble.Class))
        {
            var states = new List<ScenarioStateInfo>();
            var ordinal = 0;
            foreach (ref readonly var state in TagView.Rows(data, in bubble.States, SliceSetState.Class))
            {
                if (Class(state.Entry) != SScenarioStateEntry.Class) throw new InvalidDataException("Scenario state has an invalid entry class.");
                ref readonly var entry = ref TagView.Checked<SScenarioStateEntry>(catalog.Read(state.Entry), 0);
                // Each flag is the low byte of its word.
                states.Add(new ScenarioStateInfo(bubble.NameHash, ordinal++, state.StateHash,
                    ResolveHashName(state.StateHash).Name, state.Enabled != 0, (byte)state.PublicFlag != 0,
                    state.Entry, entry.Index, entry.Link, state.MapBubbleIndex));
            }
            bubbles.Add(new ScenarioBubbleInfo(bubble.NameHash, BubbleDisplayName(bubble.NameHash), states));
        }
        return new ScenarioInfo(tag, Name(tag), bubbles);
    }

    /** A selected state still applies each object's exact authored entry-index filter. */
    private void VisitScenario(uint tag, Matrix4x4 transform, int depth)
    {
        var described = scene.Scenario?.TagHash == tag ? scene.Scenario : DescribeScenario(tag);
        scene.Scenario = described;
        ValidateScenarioSelection(described);
        foreach (var bubble in described.Bubbles)
        {
            if (options.BubbleNameHashes != null && !options.BubbleNameHashes.Contains(bubble.NameHash)) continue;
            IEnumerable<ScenarioStateInfo> selected = options.ScenarioStates switch
            {
                ScenarioStateMode.EnabledByPackage => bubble.States.Where(s => s.PackageEnabled),
                ScenarioStateMode.Selected => options.StateOrdinals.TryGetValue(bubble.NameHash, out var ordinal)
                    ? bubble.States.Where(s => s.Ordinal == ordinal) : [],
                _ => bubble.States
            };
            var loaded = selected.ToArray();
            if (loaded.Length == 0) continue;
            scene.Bubbles.Add(new BubbleInfo(bubble.NameHash, bubble.NameHash, bubble.Name));
            foreach (var state in loaded)
            {
                scene.ScenarioStates.Add(state);
                var previous = activeEntryIndex;
                activeEntryIndex = state.EntryIndex;
                var firstInstance = scene.Instances.Count;
                var firstMarker = scene.Markers.Count;
                try { Visit(state.EntryTag, bubble.NameHash, transform, depth + 1); }
                finally
                {
                    activeEntryIndex = previous;
                    stateRanges.Add(new StateRange(tag, bubble.NameHash, state.Ordinal, state.EntryIndex,
                        firstInstance, scene.Instances.Count, firstMarker, scene.Markers.Count));
                }
            }
        }
        AddDiagnostic($"Scenario uses {options.ScenarioStates} authored states; runtime activation is not simulated.");
    }

    /** Unknown selection keys are errors; an empty explicit selection is valid. */
    private void ValidateScenarioSelection(ScenarioInfo described)
    {
        var declaredBubbles = described.Bubbles.Select(b => b.NameHash).ToHashSet();
        var selectedHashes = options.StateOrdinals.Keys.Concat(
            options.BubbleNameHashes?.AsEnumerable() ?? Enumerable.Empty<uint>());
        foreach (var hash in selectedHashes.Distinct())
            if (!declaredBubbles.Contains(hash))
                throw new ArgumentException($"Scenario 0x{described.TagHash:X8} has no authored bubble 0x{hash:X8}.");
        foreach (var bubble in described.Bubbles)
        {
            if (options.ScenarioStates == ScenarioStateMode.Selected
                && options.StateOrdinals.TryGetValue(bubble.NameHash, out var wanted)
                && bubble.States.All(s => s.Ordinal != wanted))
                throw new ArgumentException($"Bubble 0x{bubble.NameHash:X8} has no authored state {wanted}.");
        }
    }

    /** Slot ordinals belong to the object's complete slot array, not a per-type counter. */
    private void VisitAuthoredObject(uint tag, byte[] data, uint bubble, Matrix4x4 transform, int depth)
    {
        ref readonly var authored = ref TagView.Checked<SAuthoredObject>(data, 0);
        var registryKey = authored.RegistryKey;
        var slots = new List<AuthoredSlotInfo>();
        var ordinal = 0;
        foreach (ref readonly var slot in TagView.Rows(data, in authored.SlotTable.Slots, ObjectSlot.Class))
        {
            var identity = new AuthoredSlotIdentity(registryKey, checked((ushort)slot.SlotType), checked((ushort)ordinal++));
            if (slotNames.TryGetValue(identity, out var previous) && previous != slot.NameHash)
                throw new InvalidDataException("Authored slot identity has conflicting names.");
            slotNames[identity] = slot.NameHash;
            slots.Add(new AuthoredSlotInfo(identity, slot.NameHash, ResolveHashName(slot.NameHash).Name));
        }
        if (scene.AuthoredObjects.All(o => o.TagHash != tag))
            scene.AuthoredObjects.Add(new AuthoredObjectInfo(tag, registryKey, slots));
        foreach (ref readonly var filter in TagView.Rows(data, in authored.EntryFilters, ObjectBubbleBlock.Class))
        {
            if (activeEntryIndex != uint.MaxValue && filter.BubbleIndex != -1 && filter.BubbleIndex != activeEntryIndex) continue;
            foreach (ref readonly var handle in TagView.Rows(data, in filter.Handles, PlacedHandle.Class))
                Visit(handle.Handle, bubble, transform, depth + 1);
        }
    }
}
