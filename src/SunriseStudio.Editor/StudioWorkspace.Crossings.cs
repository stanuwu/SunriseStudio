using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/** One exit box of a saved connection, drawn in the world so the move and scale handles can edit it. */
public sealed record CrossingExit(string ConnectionId, bool Source, SceneMarker Marker);

public sealed partial class StudioWorkspace
{
    private IReadOnlyList<CrossingExit> crossingExits = [];
    /** Exit boxes of the open map's saved connections; a world load rebuilds them. */
    public IReadOnlyList<CrossingExit> CrossingExits => crossingExits;
    public IReadOnlyList<ProjectBubbleConnection> BubbleConnections => Project?.BubbleConnections ?? [];

    /** The open map's bubbles a connection can name, with why a bubble cannot take one. */
    public IReadOnlyList<CrossingBubble> CrossingBubbles(uint mapTag) =>
        CrossingCompiler.Bubbles(Catalog ?? throw new InvalidOperationException("Open your Sunrise installation first."), mapTag);

    public CrossingExit? FindCrossingExit(object? item) => item is SceneMarker marker
        ? crossingExits.FirstOrDefault(exit => ReferenceEquals(exit.Marker, marker)) : null;

    /**
     * Compiles the project's connections from the installed originals and saves them with their entry bytes
     * in one manifest write. An entry another project edit already changed is refused by name.
     */
    public void ApplyBubbleConnections(IReadOnlyList<ProjectBubbleConnection> connections, string action)
    {
        RequireEditable();
        var project = Project!;
        var catalog = Catalog!;
        var before = project.BubbleConnections;
        BeginWork("Compiling crossings", () =>
        {
            var previous = CrossingCompiler.Compile(catalog, before);
            var edits = CrossingCompiler.Edits(catalog, before, connections, out var compiled);
            foreach (uint tag in edits.Keys)
            {
                if (!project.ContainsEdit(tag)) continue;
                byte[] expected = previous.Assets.TryGetValue(tag, out var owned) ? owned : catalog.ReadOriginal(tag);
                if (!project.ReadAsset(tag, catalog).AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException($"Entry {tag:X8} has other project edits; a crossing writes it from the installed original.");
            }
            return () =>
            {
                MutateProject(() => project.SetBubbleConnections(connections, edits, catalog), edits.Keys);
                ProjectRevision++;
                Status = action;
                Notice = new(action, false, null, CrossingSummary(compiled));
                // The reload keeps this notice; it rebuilds the exit markers from the saved connections.
                if (World is not null) ReloadWorld(null, "Bubble connections changed.");
            };
        }, blocking: false);
    }

    private static string? CrossingSummary(CrossingCompilation compiled)
    {
        if (compiled.Reports.Count == 0) return null;
        return string.Join("\n", compiled.Reports.Select(report =>
            $"{report.Id}: link {report.LinkKey:X16}{(report.NewLink ? " added" : "")}, "
            + $"{report.Source.ExitCells} and {report.Destination.ExitCells} exit cells, "
            + $"{report.Source.RemovedZones + report.Destination.RemovedZones} existing zones removed."));
    }

    /** Called on the load worker: the saved exit boxes of this map become Transitions markers. */
    private IReadOnlyList<CrossingExit> AddCrossingExits(WorldDocument world, IReadOnlyList<ProjectBubbleConnection> connections)
    {
        var exits = new List<CrossingExit>();
        foreach (var connection in connections.Where(c => c.MapTag == world.Map.Tag))
            foreach (bool source in new[] { true, false })
            {
                var box = source ? connection.SourceExit : connection.DestinationExit;
                uint bubble = source ? connection.SourceBubble : connection.DestinationBubble;
                var marker = new SceneMarker
                {
                    Name = $"{connection.Id} exit in {ResolveHash(bubble) ?? $"{bubble:X8}"}",
                    NameEvidence = SceneNameEvidence.Derived,
                    Semantic = "crossing exit",
                    Category = "Crossing",
                    Layer = SceneLayer.Transitions,
                    SourceOffset = -1,
                    Position = new(box.Center[0], box.Center[1], box.Center[2]),
                    Extents = new(box.HalfExtents[0], box.HalfExtents[1], box.HalfExtents[2]),
                    Color = source ? new(1, .25f, .2f, 1) : new(.3f, 1, .4f, 1)
                };
                world.Scene.Markers.Add(marker);
                exits.Add(new(connection.Id, source, marker));
            }
        return exits;
    }

    /** Moves or scales one exit box about its centre and recompiles; a turn is refused. */
    public void EditCrossingExit(CrossingExit exit, TransformChange change)
    {
        if (change.Turn != Quaternion.Identity)
            throw new InvalidDataException("Exit boxes stay axis-aligned; a turned exit needs polygon portals.");
        var connections = BubbleConnections.ToArray();
        int index = Array.FindIndex(connections, c => c.Id == exit.ConnectionId);
        if (index < 0) throw new InvalidDataException($"Connection {exit.ConnectionId} is no longer saved.");
        var connection = connections[index];
        var box = exit.Source ? connection.SourceExit : connection.DestinationExit;
        var moved = new ProjectCrossingBox(
            [box.Center[0] + change.Move.X, box.Center[1] + change.Move.Y, box.Center[2] + change.Move.Z],
            [box.HalfExtents[0] * change.Scale.X, box.HalfExtents[1] * change.Scale.Y, box.HalfExtents[2] * change.Scale.Z]);
        moved.Validate();
        connections[index] = exit.Source ? connection with { SourceExit = moved } : connection with { DestinationExit = moved };
        ApplyBubbleConnections(connections, $"Moved {exit.ConnectionId} exit");
    }
}
