using System.Numerics;
using SunriseStudio.Tags.Generated;

namespace SunriseStudio.Scene;

/** One tome cluster's world box and the kind its zone-crossing record gives it. */
public readonly record struct TransitionCell(Vector3 Minimum, Vector3 Maximum, sbyte Kind);

/**
 * A bubble's placed visibility id map. The loader records where it is placed; the editor reads the
 * map's tome and zone-crossing table into Cells, because those readers sit above this project.
 */
public sealed record PlacedVisibility(uint BubbleHash, uint MapTag, uint PlacementTag, int PlacementRow, SceneMarker Marker)
{
    public IReadOnlyList<TransitionCell> Cells { get; private set; } = [];

    /** Stores the decoded cells and moves the marker onto the tome's box. */
    public void SetCells(IReadOnlyList<TransitionCell> cells, Vector3 minimum, Vector3 maximum)
    {
        Cells = cells;
        Marker.SetAuthored((minimum + maximum) / 2, Quaternion.Identity, (maximum - minimum) / 2);
    }
}

public sealed partial class AssetSceneLoader
{
    /** Switch cells draw red, near bands amber, kind 4 green, far bands blue. */
    public static Vector4 TransitionStateColor(int kind) => kind switch
    {
        2 => new(1, .25f, .2f, 1),
        3 => new(1, .7f, .2f, 1),
        4 => new(.3f, 1, .4f, 1),
        5 => new(.35f, .6f, 1, 1),
        _ => new(.5f, .5f, .5f, 1)
    };

    /** The map becomes a Transitions marker; the editor sets its box when it reads the tome. */
    private void ReadPlacedVisibility(uint mapTag, uint table, int row, uint bubble)
    {
        if (Class(mapTag) != SVisibilityTomeMap.Class)
        {
            scene.DecodeErrorCount++;
            scene.Diagnostics.Add($"Placed visibility map {mapTag:X8} has the wrong class.");
            return;
        }
        var marker = new SceneMarker
        {
            Name = $"Visibility cells {mapTag:X8}",
            SourceTag = mapTag,
            SourceOffset = -1,
            NameEvidence = SceneNameEvidence.Derived,
            Semantic = "visibility query volume",
            TagHash = mapTag,
            BubbleHash = bubble,
            Layer = SceneLayer.Transitions,
            Category = "Visibility",
            Color = new(.45f, .45f, .55f, 1)
        };
        scene.Markers.Add(marker);
        scene.Visibility.Add(new(bubble, mapTag, table, row, marker));
    }
}
