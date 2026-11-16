using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;

/** Clip events through the writer, on every installed clip that has any. */
static class ClipEventChecks
{
    /** A sound event any install has; the check only needs a tag of the right class. */
    private const uint SoundClass = 0x80809802;

    public static void Run(PackageCatalog owned)
    {
        var schema = StudioRegistry.Index;
        int clips = 0, withEvents = 0, events = 0, identical = 0, stripped = 0, plain = 0, plainIdentical = 0, added = 0;
        var refused = new SortedDictionary<string, int>();
        var kinds = new SortedDictionary<int, int>();
        uint sound = owned.Entries.First(entry => entry.Reference == SoundClass && owned.ContainsInstalledPackage(entry.PackageId)).TagHash;
        foreach (var entry in owned.Entries.Where(entry => entry.Reference == SAnimationClip.Class && owned.ContainsInstalledPackage(entry.PackageId)))
        {
            byte[] clip = owned.ReadOriginal(entry.TagHash);
            clips++;
            bool has = clip.Length >= 400 && SunriseStudio.Tags.Views.TagView.At<SAnimationClip>(clip).Events.Count > 0;
            // Every clip with events is checked; one in twenty of the rest keeps the run short.
            if (!has && clips % 20 != 0) continue;
            IReadOnlyList<ClipEvent> read;
            try { read = AnimationClipEvents.Read(clip, schema); }
            catch (NotSupportedException error)
            {
                string reason = new string(error.Message.TakeWhile(value => !char.IsDigit(value)).ToArray());
                refused[reason] = refused.GetValueOrDefault(reason) + 1;
                Check(!has || !reason.StartsWith("A clip event", StringComparison.Ordinal), $"Clip {entry.TagHash:X8} has an empty event slot.");
                continue;
            }
            byte[] again = AnimationClipEvents.Write(clip, schema, read);
            if (!has)
            {
                plain++;
                Check(read.Count == 0, "A clip without events read one.");
                if (again.AsSpan().SequenceEqual(clip)) plainIdentical++;
                continue;
            }
            withEvents++;
            events += read.Count;
            foreach (var value in read) kinds[value.Kind] = kinds.GetValueOrDefault(value.Kind) + 1;
            Check(read.Zip(read.Skip(1), (left, right) => left.Frame <= right.Frame).All(value => value), $"Clip {entry.TagHash:X8} stores events out of frame order.");
            Check(again.AsSpan().SequenceEqual(clip), $"Clip {entry.TagHash:X8} changed through the event writer.");
            identical++;
            // Remove every event, then put them back.
            byte[] empty = AnimationClipEvents.Write(clip, schema, []);
            Check(AnimationClipEvents.Read(empty, schema).Count == 0 && empty.Length < clip.Length, "Removing every event left one.");
            Check(AnimationClipEvents.Write(empty, schema, read).AsSpan().SequenceEqual(clip), $"Clip {entry.TagHash:X8} changed after its events were removed and added again.");
            stripped++;
            if (added >= 200) continue;
            // One new sound event on the middle frame joins the list in frame order; the rest stays.
            int frame = (read[0].Frame + read[^1].Frame) / 2;
            var made = ClipEvent.NewTagged(frame, sound, 0x12345678, "content\\audio\\wwise_events\\check.wwise_event");
            byte[] grown = AnimationClipEvents.Write(clip, schema, read.Append(made));
            var reread = AnimationClipEvents.Read(grown, schema);
            Check(reread.Count == read.Count + 1, "An added event did not read back.");
            int at = Enumerable.Range(0, reread.Count).Last(index => reread[index].Frame == frame && reread[index].Target == sound);
            var back = reread[at];
            Check(back.Kind == ClipEvent.TaggedKind && back.AttachmentPoint == 0x12345678
                && back.Path == "content\\audio\\wwise_events\\check.wwise_event", "An added event lost a field.");
            Check(at == read.Count(value => value.Frame <= frame), "An added event is not last of its frame.");
            var rest = reread.Where((_, index) => index != at).ToArray();
            Check(AnimationClipEvents.Write(grown, schema, rest).AsSpan().SequenceEqual(clip), "Removing the added event did not give the clip back.");
            Check(SameOutside(clip, grown, schema), "An added event changed another part of the clip.");
            added++;
        }
        Check(withEvents > 0 && identical == withEvents && stripped == withEvents, "No installed clip with events passed the writer.");
        Console.WriteLine($"PASS clip-template-with-events new-clips={TemplatesWithEvents(owned, schema)}");
        Check(added > 0, "No clip took a new event.");
        Console.WriteLine($"PASS clip-events clips={clips} with-events={withEvents} events={events} byte-identical={identical} "
            + $"removed-and-added={stripped} new-event={added} without-events={plainIdentical}/{plain} "
            + $"kinds={string.Join(",", kinds.Select(pair => $"{pair.Key}:{pair.Value}"))} "
            + $"refused={string.Join("; ", refused.Select(pair => $"{pair.Value} {pair.Key.Trim()}"))}");
    }

    /**
     * A new clip over a template that has events: the compiler takes it and the new clip has none.
     * @return How many templates gave a clip; the rest were refused for their channels or sections.
     */
    private static int TemplatesWithEvents(PackageCatalog owned, RegistryIndex schema)
    {
        const int controls = 3, frames = 12;
        var samples = Enumerable.Range(0, controls).Select(control => (IReadOnlyList<BoneTransform>)Enumerable.Range(0, frames)
            .Select(frame => new BoneTransform(System.Numerics.Quaternion.Identity, new(control, frame * 0.1f, 0), 1)).ToArray()).ToArray();
        int made = 0, tried = 0;
        foreach (var entry in owned.Entries.Where(entry => entry.Reference == SAnimationClip.Class && owned.ContainsInstalledPackage(entry.PackageId)))
        {
            byte[] clip = owned.ReadOriginal(entry.TagHash);
            if (clip.Length < 400 || SunriseStudio.Tags.Views.TagView.At<SAnimationClip>(clip).Events.Count <= 0) continue;
            if (++tried > 400 || made >= 5) break;
            byte[] built;
            try { built = SunriseStudio.Authoring.RigClipCompiler.Prepare(owned, entry.TagHash, 0x12345678, 0x0BADF00D, samples); }
            catch (Exception error) when (error is NotSupportedException or InvalidDataException) { continue; }
            Check(AnimationClipEvents.Read(built, schema).Count == 0, $"A new clip over {entry.TagHash:X8} kept its template's events.");
            made++;
        }
        Check(made > 0, "No template with events gave a new clip.");
        return made;
    }

    /** True when both clips hold the same arrays and objects apart from the event list. */
    private static bool SameOutside(byte[] left, byte[] right, RegistryIndex schema)
    {
        int events = SunriseStudio.Tags.Views.TagView.FieldOffset<SAnimationClip>(nameof(SAnimationClip.Events));
        var a = TagBlueprintReader.Read(TagDocument.Parse(left, SAnimationClip.Class, schema), schema);
        var b = TagBlueprintReader.Read(TagDocument.Parse(right, SAnimationClip.Class, schema), schema);
        a.Links.RemoveAll(link => link.Field == events);
        b.Links.RemoveAll(link => link.Field == events);
        return Same(a, b, events);
    }

    private static bool Same(TagBlueprint left, TagBlueprint right, int skip = -1)
    {
        if (left.ClassId != right.ClassId || left.Links.Count != right.Links.Count || left.Bytes.Length != right.Bytes.Length) return false;
        // Pointer fields hold layout, not content, so they are left out of the byte compare.
        byte[] a = left.Bytes.ToArray(), b = right.Bytes.ToArray();
        foreach (var link in left.Links.Append(new(skip, TagLinkKind.Array, 0, [])).Where(link => link.Field >= 0))
        {
            int size = link.Kind == TagLinkKind.Array ? 16 : 8;
            a.AsSpan(link.Field, size).Clear();
            b.AsSpan(link.Field, size).Clear();
        }
        // The root starts with the tag length, which an added event changes.
        if (skip >= 0)
        {
            a.AsSpan(0, SunriseStudio.Tags.Views.TagView.SizeWordSize).Clear();
            b.AsSpan(0, SunriseStudio.Tags.Views.TagView.SizeWordSize).Clear();
        }
        if (!a.AsSpan().SequenceEqual(b)) return false;
        for (int index = 0; index < left.Links.Count; index++)
        {
            var x = left.Links[index];
            var y = right.Links[index];
            if (x.Field != y.Field || x.Kind != y.Kind || x.ClassId != y.ClassId || x.Children.Count != y.Children.Count
                || !(x.Text ?? []).AsSpan().SequenceEqual(y.Text ?? [])) return false;
            for (int child = 0; child < x.Children.Count; child++)
                if (!Same(x.Children[child], y.Children[child])) return false;
        }
        return true;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
