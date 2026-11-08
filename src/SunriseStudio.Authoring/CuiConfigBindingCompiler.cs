using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SunriseStudio.Formats;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Document;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Authoring;

/** Only verified CUI scalar leaves are config sites; structural words and hashes are excluded. */
public sealed partial class CuiConfigBindingCompiler : IConfigBindingCompiler
{
    public static CuiConfigBindingCompiler Default { get; } = new();
    private static readonly HashSet<ulong> LayoutTypes = [CuiTypeRegistry.FrameType, CuiTypeRegistry.TextType,
        CuiTypeRegistry.BitmapType, CuiTypeRegistry.ContainerType];

    public IReadOnlyList<ConfigBindingCandidate> FindSites(uint tag, uint classHash, ReadOnlyMemory<byte> bytes)
    {
        if (classHash != CuiResource.Class) return [];
        if (bytes.Length > ConfigBindingLimits.MaximumEntryBytes)
            throw new InvalidDataException("A bound CUI entry must fit 64 MiB.");
        var document = CuiDocument.Read(tag, classHash, bytes);
        int boolPool = TagView.FieldOffset<CuiResource>("ValuePool.BoolValues");
        int floatPoolA = TagView.FieldOffset<CuiResource>("ValuePool.PoolFloatsA");
        int floatPoolB = TagView.FieldOffset<CuiResource>("ValuePool.PoolFloatsBNormalized");
        List<ConfigBindingCandidate> result = [];
        for (int ownerIndex = 0; ownerIndex < document.Objects.Count; ownerIndex++)
        {
            var owner = document.Objects[ownerIndex];
            for (int componentIndex = 0; componentIndex < owner.Components.Count; componentIndex++)
            {
                var component = owner.Components[componentIndex];
                var nodes = document.Nodes.Where(node => node.Id == component.Key).ToArray();
                if (nodes.Length != 1) continue;
                var node = nodes[0];
                ulong type = ((ulong)node.AuxiliaryTypeHash << 32) | node.TypeHash;
                foreach (var property in component.Properties)
                {
                    if (!property.Direct) continue;
                    if (!ReferenceEquals(property, component.Properties.Last(value => value.Direct && value.Selector == property.Selector))) continue;
                    ModConfigType kind;
                    string label;
                    uint bits;
                    if (type == CuiTypeRegistry.TextType && property.Selector == 0x41A
                        && property.PoolDescriptor == boolPool && property.ValueClass == SCuiBoolValue.Class)
                    {
                        bits = bytes.Span[property.ValueOffset];
                        if (bits > 1) continue;
                        kind = ModConfigType.Bool; label = "Auto width";
                    }
                    else if (LayoutTypes.Contains(type) && property.Selector is >= 0x206 and <= 0x209
                        && (property.PoolDescriptor == floatPoolA || property.PoolDescriptor == floatPoolB) && property.ValueClass == SCuiFloatValue.Class)
                    {
                        bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span[property.ValueOffset..]);
                        if (!float.IsFinite(BitConverter.UInt32BitsToSingle(bits))) continue;
                        kind = ModConfigType.Float;
                        label = CuiDocument.PropertyName(property.Selector, true);
                    }
                    else continue;
                    string guard = Hash($"property/{ownerIndex}/{componentIndex}/{component.Key}/{type:X16}/{property.Selector:X16}/{property.PoolDescriptor}");
                    result.Add(new(new(ConfigBindingLimits.PropertyProfile, property.RecordOffset, ownerIndex,
                        componentIndex, property.PoolDescriptor, guard, bits,
                        $"Node {node.Id}: {label} (profile {ownerIndex})"), kind, property.ValueOffset));
                }
            }
        }
        foreach (var binding in CuiBindings.Read(bytes))
        {
            if (binding.Error.Length != 0 || binding.Transform.Kind is not
                (CuiBindingTransformKind.CompareInteger or CuiBindingTransformKind.CompareFloat)) continue;
            var kind = binding.Transform.Kind == CuiBindingTransformKind.CompareInteger ? ModConfigType.Int : ModConfigType.Float;
            if (kind == ModConfigType.Float && !float.IsFinite(BitConverter.UInt32BitsToSingle(binding.Transform.Operand))) continue;
            string guard = Hash($"comparison/{binding.Source.Node}/{binding.Source.AuthoredNode}/{binding.Source.PathKey}/"
                + $"{binding.Target.Node}/{binding.Target.AuthoredNode}/{binding.Target.PathKey}/"
                + $"{binding.Transform.ClassHash:X8}/{binding.Transform.Kind}/{binding.Transform.Comparison}");
            int operand = binding.Transform.Offset + (kind == ModConfigType.Int
                ? TagView.FieldOffset<CuiCompareInteger>(nameof(CuiCompareInteger.Operand)) : TagView.FieldOffset<CuiCompareFloat>(nameof(CuiCompareFloat.Operand)));
            result.Add(new(new(ConfigBindingLimits.ComparisonProfile, binding.Offset, -1, -1, -1,
                guard, binding.Transform.Operand, $"Binding {binding.Index}: {binding.Transform.Kind} operand"), kind, operand));
        }
        return result.AsReadOnly();
    }

    /** Every site receives private output storage even when the default equals the saved literal. */
    public ConfigBindingCompilation Compile(IReadOnlyList<ProjectConfigBinding> bindings,
        IReadOnlyList<ModConfigEntry> settings, Func<uint, byte[]> readSource)
    {
        Dictionary<uint, byte[]> assets = [];
        List<ModConfigSite> sites = [];
        foreach (var group in bindings.GroupBy(binding => binding.TagHash).OrderBy(group => group.Key))
        {
            byte[] source = readSource(group.Key);
            var available = FindSites(group.Key, CuiResource.Class, source);
            var selected = group.OrderBy(binding => binding.Site.Identity, StringComparer.Ordinal).ToArray();
            foreach (var binding in selected)
            {
                var matches = available.Where(candidate => candidate.Site.Identity == binding.Site.Identity).ToArray();
                if (matches.Length != 1 || matches[0].Type != binding.Type
                    || matches[0].Site.GuardSha256 != binding.Site.GuardSha256
                    || matches[0].Site.PoolOffset != binding.Site.PoolOffset
                    || matches[0].Site.LiteralBits != binding.Site.LiteralBits)
                    throw new InvalidDataException($"Config site '{binding.Site.Name}' changed. Unbind or rebind it before editing.");
            }
            byte[] output = source.ToArray();
            var positions = IsolateProperties(ref output, group.Key, selected);
            foreach (var binding in selected.Where(binding => binding.Site.Profile == ConfigBindingLimits.ComparisonProfile))
            {
                var old = CuiBindings.Read(source).Single(row => row.Offset == binding.Site.RowOffset);
                int size = old.Transform.Kind == CuiBindingTransformKind.CompareInteger
                    ? Unsafe.SizeOf<CuiCompareInteger>() : Unsafe.SizeOf<CuiCompareFloat>();
                int record = TagRelocator.AppendObject(ref output, old.Transform.ClassHash, size, 8);
                RequireSize(output);
                source.AsSpan(old.Transform.Offset, size).CopyTo(output.AsSpan(record));
                int conversion = TagView.OffsetOf(output, in TagView.At<CuiBindingRow>(output, binding.Site.RowOffset).Conversion);
                Put64(output, conversion, record - conversion);
                int operand = old.Transform.Kind == CuiBindingTransformKind.CompareInteger
                    ? TagView.FieldOffset<CuiCompareInteger>(nameof(CuiCompareInteger.Operand)) : TagView.FieldOffset<CuiCompareFloat>(nameof(CuiCompareFloat.Operand));
                positions.Add(binding.Site.Identity, (binding.Site.RowOffset, record + operand));
            }
            foreach (var binding in selected)
            {
                var setting = settings.Single(value => value.Key == binding.Key && value.Type == binding.Type);
                WriteDefault(output, positions[binding.Site.Identity].Value, setting);
            }
            Put64(output, SizeWord, output.Length);
            _ = CuiDocument.Read(group.Key, CuiResource.Class, output);
            string digest = BinaryDelta.Hash(output);
            foreach (var binding in selected)
            {
                var (row, value) = positions[binding.Site.Identity];
                bool property = binding.Site.Profile == ConfigBindingLimits.PropertyProfile;
                sites.Add(new(binding.Key, binding.Site.Profile, group.Key, CuiResource.Class, output.Length,
                    digest, value, binding.Type, property ? row : null,
                    property ? binding.Site.PoolOffset : null, property ? null : row));
            }
            assets.Add(group.Key, output);
        }
        return new(assets, sites.AsReadOnly());
    }

    private static void WriteDefault(byte[] output, int at, ModConfigEntry setting)
    {
        if (setting.Type == ModConfigType.Bool && ModManifest.TryParseBool(setting.Default, out bool flag))
            output[at] = flag ? (byte)1 : (byte)0;
        else if (setting.Type == ModConfigType.Int && ModManifest.TryParseInt(setting.Default, out int value))
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at), value);
        else if (setting.Type == ModConfigType.Float && ModManifest.TryParseFloat(setting.Default, out float real))
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at), real);
        else throw new InvalidDataException("Config default is invalid for its declared type.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Put64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
    private static void RequireSize(byte[] bytes)
    {
        if (bytes.Length > ConfigBindingLimits.MaximumEntryBytes) throw new InvalidDataException("A compiled CUI entry exceeds 64 MiB.");
    }
    private static readonly int SizeWord = TagView.FieldOffset<CuiResource>(nameof(CuiResource.TagSize));
}
