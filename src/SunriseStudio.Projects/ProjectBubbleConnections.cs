using System.Text.Json.Serialization;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/** How a connection lays out its crossing cells. */
public enum CrossingProfile
{
    /** The bubble's query volume becomes one zone of box cells: own exit 2, middle 3, other exit 4, rest 5. */
    Retail,
    /** Installed cells under the own exit trigger at once; cells under the other exit become a timed landing band. */
    Simple
}

/** What a connection does with the crossings its two bubbles already hold. */
public enum InheritedZonePolicy { Keep, Deactivate, Retarget }

/** Only an open path walks across; the other kinds have no native compiler. */
public enum CrossingTravel { OpenPath, Door, Teleport, Scripted }

/** An axis-aligned world box; a set Rotation is refused, because a turned exit needs polygon portals. */
public sealed record ProjectCrossingBox(float[] Center, float[] HalfExtents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float[]? Rotation = null)
{
    public void Validate()
    {
        if (Center is not { Length: 3 } || HalfExtents is not { Length: 3 } || !Center.All(float.IsFinite)
            || !HalfExtents.All(value => float.IsFinite(value) && value > 0) || Rotation is { Length: not 4 })
            throw new InvalidDataException("A crossing exit box needs a finite centre and positive half extents.");
    }
}

/**
 * One authored crossing between two bubbles of a map, by bubble name hash. Each exit is where a player leaves
 * its bubble toward the other. The compiler derives every byte from the installed originals; none are saved here.
 */
public sealed record ProjectBubbleConnection(string Id, uint MapTag, uint SourceBubble, uint DestinationBubble,
    ProjectCrossingBox SourceExit, ProjectCrossingBox DestinationExit,
    CrossingProfile Profile = CrossingProfile.Retail, InheritedZonePolicy Policy = InheritedZonePolicy.Keep,
    CrossingTravel Travel = CrossingTravel.OpenPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? EntryPoint = null)
{
    public const int MaximumIdLength = 64, MaximumConnections = 256;

    /** Checks what the manifest can check without the installation. */
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > MaximumIdLength || Id.Any(char.IsControl) || Id != Id.Trim())
            throw new InvalidDataException("Bubble connection id is invalid.");
        if (SourceBubble == DestinationBubble) throw new InvalidDataException("A bubble connection needs two different bubbles.");
        if (!Enum.IsDefined(Profile) || !Enum.IsDefined(Policy) || !Enum.IsDefined(Travel))
            throw new InvalidDataException("Bubble connection kind is invalid.");
        if (SourceExit is null || DestinationExit is null) throw new InvalidDataException("A bubble connection needs two exit boxes.");
        SourceExit.Validate();
        DestinationExit.Validate();
    }

    /** The unordered pair; one connection per pair. */
    [JsonIgnore] public (uint, uint) Pair => SourceBubble < DestinationBubble ? (SourceBubble, DestinationBubble) : (DestinationBubble, SourceBubble);
}

public sealed partial class StudioProject
{
    public IReadOnlyList<ProjectBubbleConnection> BubbleConnections => manifest.BubbleConnections ?? [];

    /**
     * Saves the connections and the entry bytes compiled from them in one manifest write.
     * @param edits Every entry the old or new connections touch; bytes equal to the original remove its change.
     */
    public void SetBubbleConnections(IReadOnlyList<ProjectBubbleConnection> connections, IReadOnlyDictionary<uint, byte[]> edits,
        PackageCatalog catalog)
    {
        var frozen = Array.AsReadOnly(connections.ToArray());
        RequireBubbleConnections(frozen);
        CommitTransaction([], _ => new(edits), null, catalog, connections: frozen);
    }

    private static void RequireBubbleConnections(IReadOnlyList<ProjectBubbleConnection> connections)
    {
        if (connections.Count > ProjectBubbleConnection.MaximumConnections)
            throw new InvalidDataException("A project holds at most 256 bubble connections.");
        foreach (var connection in connections)
        {
            if (connection is null) throw new InvalidDataException("Unsupported project manifest.");
            connection.Validate();
        }
        if (connections.Select(connection => connection.Id).Distinct(StringComparer.Ordinal).Count() != connections.Count)
            throw new InvalidDataException("Project has two bubble connections with one id.");
    }
}
