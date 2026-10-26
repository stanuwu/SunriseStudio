using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Solforge.Mcp.Data;
using SunriseStudio.Audio;
using SunriseStudio.Packages;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Editor;

/**
 * The MCP middleware decoders (havok_packfile, export_collision_obj, audio_info, export_audio_wav, video_info,
 * export_video) over the open installation. Runs on the MCP worker; reads go through the catalog's own locks and
 * nothing here touches the UI. Exports write only to the folder the tool passes (the project's exports folder).
 */
public sealed partial class StudioForm
{
    /** The MCP media host: Havok, Wwise and USM decoders for the media tools. */
    private sealed partial class AiHost : ISolforgeMediaHost
    {
        private const int HavokType = 26, HavokSubType = 7, UsmType = 27;

        /** Formats a tag hash as 0x%08X. */
        private static string Hex(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
        /** A vector as a JSON array rounded to four places. */
        private static JsonArray V3(Vector3 v) => new(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));

        /** Loads the Havok packfile of a tag (the original game bytes or the project version); null when it is not one. */
        private HavokPackfile? Havok(uint tag, bool original)
        {
            if (DataCatalog is not { } catalog || !catalog.TryGetEntry(tag, out var entry)) return null;
            byte[] bytes = original ? catalog.ReadOriginal(tag) : catalog.Read(tag);
            if (!HavokPackfile.IsPackfile(bytes))
                throw new InvalidDataException($"0x{tag:X8} ({entry.Type}/{entry.SubType}) is not a Havok packfile; packfiles are type {HavokType}/{HavokSubType}.");
            return HavokPackfile.Parse(bytes);
        }

        /** Implements havok_packfile. */
        public JsonObject? DescribeHavok(uint tag, bool original, int maxShapes)
        {
            if (Havok(tag, original) is not { } file) return null;
            var inspection = HavokInspector.Inspect(file);
            var shapes = new JsonArray();
            foreach (var shape in inspection.Shapes.Take(maxShapes))
                shapes.Add(new JsonObject
                {
                    ["offset"] = "0x" + shape.Offset.ToString("X", CultureInfo.InvariantCulture), ["class"] = shape.Class,
                    ["vertices"] = shape.Vertices, ["triangles"] = shape.Triangles, ["min"] = V3(shape.Min), ["max"] = V3(shape.Max),
                    ["undecoded"] = shape.Undecoded, ["error"] = shape.Error
                });
            var cloth = new JsonArray();
            foreach (var collidable in inspection.Cloth)
                cloth.Add(new JsonObject
                {
                    ["name"] = collidable.Name, ["shape"] = collidable.ShapeClass, ["drawn"] = collidable.Shape is not null,
                    ["position"] = V3(collidable.Transform.Translation)
                });
            var classes = new JsonObject();
            foreach (var (name, count) in inspection.ClassCounts) classes[name] = count;
            return new JsonObject
            {
                ["tag"] = Hex(tag), ["user_tag"] = inspection.UserTag, ["root"] = inspection.RootClass, ["kind"] = inspection.Kind,
                ["objects"] = inspection.Objects.Count, ["classes"] = classes,
                ["imports"] = new JsonArray([.. inspection.Imports.Select(import => (JsonNode)new JsonObject
                    { ["field"] = "0x" + import.Field.ToString("X", CultureInfo.InvariantCulture), ["name"] = import.Name })]),
                ["shapes_total"] = inspection.Shapes.Count,
                ["shapes_undecoded"] = inspection.Shapes.Count(shape => shape.Undecoded is not null || shape.Error is not null),
                ["shapes"] = shapes, ["cloth_collidables"] = cloth,
                ["placed_shapes"] = inspection.Placed.Count, ["placed_triangles"] = inspection.Placed.Sum(item => item.Shape.TriangleCount),
                ["notes"] = new JsonArray([.. inspection.Notes.Select(note => (JsonNode)note)])
            };
        }

        /** Implements export_collision_obj. */
        public JsonObject? ExportHavokObj(uint tag, bool original, string path)
        {
            if (Havok(tag, original) is not { } file) return null;
            var notes = new List<string>();
            var placed = HavokInspector.Placed(file, null, notes);
            int triangles;
            using (var writer = new StreamWriter(path)) triangles = HavokInspector.WriteObj(placed, writer, Hex(tag));
            return new JsonObject
            {
                ["path"] = path, ["shapes"] = placed.Count, ["triangles"] = triangles,
                ["notes"] = new JsonArray([.. notes.Select(note => (JsonNode)note)])
            };
        }

        /** Loads the audio document of a tag, or null. */
        private AudioDocument? Audio(uint tag)
        {
            if (DataCatalog is not { } catalog || !catalog.TryGetEntry(tag, out var entry)) return null;
            if (!AudioCatalog.Supports(entry))
                throw new InvalidDataException($"0x{tag:X8} ({entry.Type}/{entry.SubType}) is not Wwise audio: give an event (class 0x80809802), bank (26/5) or stream (26/6).");
            return new AudioCatalog(catalog, catalog.GetName).Read(tag);
        }

        /** Implements audio_info. */
        public Task<JsonObject?> DescribeAudio(uint tag, CancellationToken cancel) => Task.Run(() =>
        {
            if (DataCatalog is not { } catalog || Audio(tag) is not { } document) return null;
            var reader = new AudioCatalog(catalog);
            var media = new JsonArray();
            foreach (var item in document.Media)
            {
                cancel.ThrowIfCancellationRequested();
                var row = new JsonObject { ["id"] = item.Id, ["tag"] = item.Tag is { } stream ? Hex(stream) : null, ["name"] = item.Name, ["size"] = item.Size };
                try
                {
                    var info = WemDecoder.Inspect(reader.ReadMedia(item));
                    row["codec"] = info.Encoding; row["sample_rate"] = info.SampleRate; row["channels"] = info.Channels;
                    row["seconds"] = Math.Round(info.DurationSeconds, 3);
                }
                catch (Exception error) when (error is AudioUnsupportedException or InvalidDataException or IOException)
                { row["unsupported"] = error.Message; }
                media.Add(row);
            }
            return (JsonObject?)new JsonObject
            {
                ["tag"] = Hex(tag), ["name"] = document.Name, ["kind"] = document.Kind, ["event_id"] = document.EventId,
                ["bank"] = document.Bank is { } bank ? new JsonObject { ["tag"] = Hex(bank.Tag), ["id"] = bank.Id, ["version"] = bank.Version, ["objects"] = bank.Objects.Count } : null,
                ["media"] = media, ["diagnostics"] = new JsonArray([.. document.Diagnostics.Select(text => (JsonNode)text)])
            };
        }, cancel);

        /** Implements export_audio_wav. */
        public Task<JsonObject?> ExportAudioWav(uint tag, uint? mediaId, string folder, CancellationToken cancel) => Task.Run(() =>
        {
            if (DataCatalog is not { } catalog || Audio(tag) is not { } document) return null;
            var reader = new AudioCatalog(catalog);
            var chosen = document.Media.Where(item => mediaId is null || item.Id == mediaId).ToList();
            if (chosen.Count == 0) throw new InvalidDataException(mediaId is null ? "The tag has no media." : $"Media {mediaId} is not in this tag; see audio_info.");
            Directory.CreateDirectory(folder);
            var files = new JsonArray();
            var skipped = new JsonArray();
            foreach (var item in chosen)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var wave = WemDecoder.Decode(reader.ReadMedia(item), 0, cancel);
                    string path = Path.Combine(folder, $"{item.Id}.wav");
                    AudioStemExport.Write(path, wave);
                    files.Add(new JsonObject { ["media"] = item.Id, ["path"] = path, ["channels"] = wave.Channels, ["seconds"] = Math.Round(wave.DurationSeconds, 3) });
                }
                catch (Exception error) when (error is AudioUnsupportedException or InvalidDataException or IOException)
                { skipped.Add(new JsonObject { ["media"] = item.Id, ["reason"] = error.Message }); }
            }
            return (JsonObject?)new JsonObject { ["files"] = files, ["skipped"] = skipped };
        }, cancel);

        /** Implements retail_port_audio: raw retail Wwise media bytes, not from a Sol package. */
        public JsonObject? ExportWwiseBytes(byte[] bytes, uint tag, string folder, CancellationToken cancel)
        {
            var info = WemDecoder.Inspect(bytes);
            var wave = WemDecoder.Decode(bytes, 0, cancel);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"{tag:X8}.wav");
            AudioStemExport.Write(path, wave);
            return new JsonObject
            {
                ["tag"] = Hex(tag), ["file"] = path, ["codec"] = info.Encoding,
                ["sample_rate"] = wave.SampleRate, ["channels"] = wave.Channels,
                ["seconds"] = Math.Round(wave.DurationSeconds, 3), ["bytes"] = bytes.Length
            };
        }

        /** Reads the USM bytes of a tag, or null. */
        private byte[]? Usm(uint tag)
        {
            if (DataCatalog is not { } catalog || !catalog.TryGetEntry(tag, out var entry)) return null;
            if (entry.Type != UsmType) throw new InvalidDataException($"0x{tag:X8} ({entry.Type}/{entry.SubType}) is not a USM cutscene; those are type {UsmType}.");
            byte[] bytes = catalog.ReadOriginal(tag);
            return UsmContainer.IsUsm(bytes) ? bytes : throw new InvalidDataException($"0x{tag:X8} has no CRID header.");
        }

        /** Implements video_info. */
        public Task<JsonObject?> DescribeVideo(uint tag, CancellationToken cancel) => Task.Run(() =>
        {
            if (Usm(tag) is not { } bytes) return null;
            var info = UsmContainer.Inspect(bytes);
            var streams = new JsonArray();
            foreach (var stream in info.Streams)
            {
                var row = new JsonObject
                {
                    ["kind"] = stream.Kind, ["channel"] = stream.Channel, ["codec"] = stream.Codec, ["chunks"] = stream.DataChunks, ["bytes"] = stream.DataBytes
                };
                if (stream.Header is { } header)
                {
                    if (stream.Kind == "video")
                    {
                        row["width"] = header.Long("width"); row["height"] = header.Long("height"); row["frames"] = header.Long("total_frames");
                        long numerator = header.Long("framerate_n"), denominator = Math.Max(1, header.Long("framerate_d"));
                        row["fps"] = Math.Round((double)numerator / denominator, 3);
                        row["seconds"] = numerator > 0 ? (JsonNode)Math.Round(header.Long("total_frames") * (double)denominator / numerator, 3) : null;
                    }
                    else if (stream.Kind == "audio")
                    {
                        row["sample_rate"] = header.Long("sampling_rate"); row["channels"] = header.Long("num_channels");
                        long rate = Math.Max(1, header.Long("sampling_rate"));
                        row["seconds"] = Math.Round(header.Long("total_samples") / (double)rate, 3);
                    }
                }
                streams.Add(row);
            }
            return (JsonObject?)new JsonObject
            {
                ["tag"] = Hex(tag), ["file"] = info.FileName, ["size"] = bytes.Length, ["looks_encrypted"] = info.LooksEncrypted, ["streams"] = streams
            };
        }, cancel);

        /** Implements export_video. */
        public Task<JsonObject?> ExportVideo(uint tag, string folder, int? audioChannel, bool includeVideo, CancellationToken cancel) => Task.Run(() =>
        {
            if (Usm(tag) is not { } bytes) return null;
            var files = UsmExport.Export(bytes, folder, tag.ToString("X8", CultureInfo.InvariantCulture), true, audioChannel, includeVideo, cancel);
            return (JsonObject?)new JsonObject
            {
                ["files"] = new JsonArray([.. files.Select(path => (JsonNode)new JsonObject { ["path"] = path, ["bytes"] = new FileInfo(path).Length })])
            };
        }, cancel);
    }
}
