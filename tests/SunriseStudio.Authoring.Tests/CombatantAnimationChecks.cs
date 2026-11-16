using SunriseStudio.Animation;
using SunriseStudio.Packages;

/** Checks the combatant clip table and the pose-to-control converter on the installed Dreg. */
public static class CombatantAnimationChecks
{
    private const uint Dreg = 0x80C1AF8A, DregDefinition = 0x80FEF6B7;
    // The limb solve sets knee and elbow roll itself, so those bones keep a few millimetres and degrees.
    private const float PositionTolerance = 0.007f, AngleTolerance = 10f;

    /** Runs every check against an installed game; throws on the first failure. */
    public static void Run(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        Slots(catalog);
        RoundTrip(catalog);
        BoneForm(catalog);
    }

    /** The Dreg's selector lists every query key, and the spelled keys reach the expected clips. */
    private static void Slots(PackageCatalog catalog)
    {
        var set = CombatantAnimationSlots.Read(catalog, Dreg) ?? throw new InvalidDataException("The Dreg has no selector.");
        Check(set.DefinitionTag == DregDefinition && set.Queries.Count == 105 && set.NameRowCount == 586
            && set.ClipSlotCount == 688, "The Dreg's selector shape changed.");
        Check(set.Queries.Sum(q => q.Rows.Count) > 0 && set.OtherRows.Count + set.Queries.SelectMany(q => q.Rows)
            .Select(r => r.NameRow).Distinct().Count() == 586, "Reached and other rows do not cover the definition.");
        var flinch = set.Queries.Single(q => q.Name == "flinch");
        Check(flinch.Behaviour == CombatantBehaviour.Flinch && flinch.Rows.Select(r => r.ClipTag).SequenceEqual(
            [0x80C0FDB5u, 0x80C0FE0Eu, 0x80C0FE11u, 0x80C0FE0Du]), "The flinch key no longer reaches its four clips.");
        int spelled = set.Queries.Count(q => q.Name is not null);
        Check(spelled == 23, $"Expected 23 spelled keys, found {spelled}.");
        Check(set.OtherRows.Any(r => r.RowName == "combat_rifle_any_idle" && r.ClipTag == 0x80C1A913),
            "The idle row is missing.");
        Console.WriteLine($"PASS combatant-slots keys={set.Queries.Count} spelled={spelled} rows={set.NameRowCount} other={set.OtherRows.Count}");
    }

    /** Every Dreg base clip, sampled to poses and converted back, draws the same pose. */
    private static void RoundTrip(PackageCatalog catalog)
    {
        var skeleton = new SkeletonReader(catalog).Read(Dreg);
        var binding = new AnimationRigReader(catalog).Read(skeleton);
        var reader = new AnimationControlReader(catalog);
        int clips = 0;
        float position = 0, angle = 0;
        foreach (var reference in new EntityAnimationCatalog(catalog).Read(Dreg))
        {
            var clip = reader.Read(reference.ClipTag);
            if (clip.PoseMode != 0) continue;
            var (pose, _) = CombatantAnimationConverter.RoundTrip(binding, skeleton, clip);
            Check(pose.MaximumPosition <= PositionTolerance && pose.MaximumRotationDegrees <= AngleTolerance,
                $"Clip {clip.TagHash:X8} misses by {pose.MaximumPosition * 1000:F2} mm, {pose.MaximumRotationDegrees:F2} deg.");
            position = Math.Max(position, pose.MaximumPosition);
            angle = Math.Max(angle, pose.MaximumRotationDegrees);
            clips++;
        }
        Check(clips > 400, "Too few Dreg base clips were read.");
        var plain = CombatantAnimationConverter.RoundTrip(binding, skeleton, reader.Read(0x80FE3213), 0).Pose;
        Check(plain.MaximumPosition > PositionTolerance, "The plain inverse now meets the tolerance; drop the refinement.");
        Console.WriteLine($"PASS combatant-pose-roundtrip clips={clips} max={position * 1000:F2}mm {angle:F2}deg plain={plain.MaximumPosition * 1000:F1}mm");
    }

    /** The one-control-per-bone form gives the bind locals back for the bind pose. */
    private static void BoneForm(PackageCatalog catalog)
    {
        var skeleton = new SkeletonReader(catalog).Read(Dreg);
        var bind = skeleton.Bones.Select(bone => bone.BindObject).ToArray();
        var controls = CombatantAnimationConverter.PosesToBoneControls(skeleton, [bind, bind]);
        float error = 0;
        for (int bone = 0; bone < controls.Length; bone++)
            foreach (var value in controls[bone])
            {
                error = Math.Max(error, (value.Translation - skeleton.Bones[bone].BindLocal.Translation).Length());
                error = Math.Max(error, CombatantAnimationConverter.Degrees(value.Rotation, skeleton.Bones[bone].BindLocal.Rotation) / 1000);
            }
        Check(error < 0.0001f, $"Bone form misses the bind locals by {error}.");
        Console.WriteLine($"PASS combatant-bone-form bones={controls.Length} error={error:G3}");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
