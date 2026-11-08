using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Tags.Generated;
using SunriseStudio.Tags.Views;

namespace SunriseStudio.Editor.Creation;

public sealed partial class VendorWorkflow
{
    /** Allocation, paired indices, strings and interaction changes share one manifest publication. */
    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        if (Problems(workspace) is { Count: > 0 } problems) throw new InvalidDataException(string.Join("\n", problems));
        var investment = workspace.Investment!;
        var currentProject = project!; var catalog = packages!; var selected = source!;
        var inputs = new Dictionary<uint, byte[]> { [selected.Tag] = definition, [selected.DisplayTag] = display };
        if (interactionSite is { } chosen) inputs.Add(chosen.Tag, interactionBytes);
        var originals = new Dictionary<uint, byte[]>();
        byte[] Read(uint tag)
        {
            byte[] bytes = catalog.Read(tag);
            if (inputs.TryGetValue(tag, out var before) && !bytes.AsSpan().SequenceEqual(before))
                throw new InvalidDataException("Vendor inputs changed during preparation.");
            inputs[tag] = bytes;
            return bytes;
        }
        void Unchanged(long expectedGeneration)
        {
            if (!ContextCurrent() || catalog.ReadGeneration != expectedGeneration
                || !File.ReadAllBytes(currentProject.ManifestPath).AsSpan().SequenceEqual(manifest)
                || catalog.GetPackageFileIdentity() != packageIdentity
                || inputs.Any(pair => !catalog.Read(pair.Key).AsSpan().SequenceEqual(pair.Value))
                || originals.Any(pair => !catalog.ReadOriginal(pair.Key).AsSpan().SequenceEqual(pair.Value))
                || catalog.ReadGeneration != expectedGeneration)
                throw new InvalidDataException("Vendor sources changed. Reopen the workflow.");
        }
        long initialGeneration = retryGeneration ?? generation;
        Unchanged(initialGeneration); generation = initialGeneration; retryGeneration = null;
        var edits = new Dictionary<uint, byte[]>();
        var rows = investment.RewriteVendorStock(selected.Tag, definition, selected.DisplayTag, display, Drafts());
        byte[] game = rows.GetValueOrDefault(selected.Tag) ?? definition.ToArray();
        byte[] text = rows.GetValueOrDefault(selected.DisplayTag) ?? display.ToArray();
        bool clone = editing is null;
        ushort packageId = clone || TextChanged ? PackageId(package) : (ushort)0;
        int first = clone || TextChanged ? NextEntryIndex(currentProject, packageId) : 0;
        int next = first + (clone ? 2 : 0);
        string title = name.Text.Trim();
        var additions = new List<ProjectAssetAddition>(); var expected = new List<uint>();
        if (TextChanged)
        {
            Read(sources.StringBanks);
            if (stringTemplate is { } template) { Read(template.ContainerTag); Read(template.DataTag); }
            foreach (var asset in currentProject.AddedAssets.Where(asset => asset.Native is { } native
                && native.PackageId == packageId && native.Reference == StringBankWriter.ContainerClass))
            {
                byte[] container = Read(TagOf(asset.Native!.PackageId, asset.Native.EntryIndex ?? -1));
                Read(TagView.At<uint>(container, TagView.FieldOffset<SLocalizedStrings>(nameof(SLocalizedStrings.LanguageData))));
            }
            var bank = ProjectStringBank.Plan(catalog, currentProject, packageId, next, Texts(), BankCandidates(), sources);
            additions.AddRange(bank.Additions); expected.AddRange(bank.AddedTags);
            foreach (var pair in bank.Edits) edits.Add(pair.Key, pair.Value);
            if (NameChanged) text = InvestmentVendorAuthoring.SetText(text, selected.Hash,
                InvestmentVendorAuthoring.NameOffset, bank.BankIndex, StringBankWriter.Hash(title));
            if (SubtitleChanged && subtitle.Text.Trim().Length != 0)
                text = InvestmentVendorAuthoring.SetText(text, selected.Hash, InvestmentVendorAuthoring.SubtitleOffset,
                    bank.BankIndex, StringBankWriter.Hash(subtitle.Text.Trim()));
        }
        if (SubtitleChanged && subtitle.Text.Trim().Length == 0)
        {
            var pair = InvestmentVendorAuthoring.ReadText(text, selected.Hash, InvestmentVendorAuthoring.SubtitleOffset);
            int bank = pair.Bank <= short.MaxValue ? pair.Bank : 0;
            text = InvestmentVendorAuthoring.SetText(text, selected.Hash, InvestmentVendorAuthoring.SubtitleOffset,
                bank, StringHash.Empty);
        }
        if (icon.Tag is { } iconTag)
        {
            var other = investment.FindVendor(iconTag) ?? throw new InvalidDataException("The selected icon vendor is missing.");
            text = InvestmentVendorAuthoring.CopyIcon(text, selected.Hash, Read(other.DisplayTag), other.Hash);
        }
        uint hash = selected.Hash;
        if (clone)
        {
            uint gameTag = TagOf(packageId, first), textTag = TagOf(packageId, first + 1);
            byte[] index = Read(sources.Vendors), displayIndex = Read(sources.VendorDisplays);
            var used = InvestmentItemClone.ReadIndexRows(index).Select(row => row.Hash)
                .Concat(InvestmentItemClone.ReadIndexRows(displayIndex).Select(row => row.Hash)).ToHashSet();
            hash = StringHash.NewDefinitionHash($"sunrise:vendor:{currentProject.Name}:{packageId:X4}:{first}:{title}", used);
            (game, text) = InvestmentVendorAuthoring.Derive(game, text, selected.Hash, hash);
            var (newIndex, position) = InvestmentItemClone.AppendIndexRow(index, hash, gameTag);
            var (newDisplayIndex, otherPosition) = InvestmentItemClone.AppendIndexRow(displayIndex, hash, textTag);
            if (position != otherPosition || position >= InvestmentVendorAuthoring.MaximumVendors)
                throw new InvalidDataException("The new vendor indices are unpaired or exceed the native range.");
            edits.Add(sources.Vendors, newIndex); edits.Add(sources.VendorDisplays, newDisplayIndex);
            if (!catalog.TryGetEntry(selected.Tag, out var gameEntry) || !catalog.TryGetEntry(selected.DisplayTag, out var textEntry))
                throw new InvalidDataException("Vendor source metadata is missing.");
            string stem = FileName(title);
            additions.InsertRange(0,
            [
                new($"{stem}.vendor", game, new(packageId, InvestmentCatalog.VendorClass, gameEntry.TypeInfo, stem, first), selected.Tag),
                new($"{stem}.vendor-display", text, new(packageId, InvestmentVendorAuthoring.DisplayClass, textEntry.TypeInfo,
                    stem + "_display", first + 1), selected.DisplayTag)
            ]);
            expected.InsertRange(0, [gameTag, textTag]);
        }
        else { edits[selected.Tag] = game; edits[selected.DisplayTag] = text; }
        if (interactionSite is { } site)
            edits[site.Tag] = InvestmentVendorAuthoring.RetargetInteraction(catalog, interactionBytes, site, hash);
        foreach (uint tag in additions.Where(asset => asset.BaseTagHash is not null).Select(asset => asset.BaseTagHash!.Value).Distinct())
            originals.Add(tag, catalog.ReadOriginal(tag));
        Unchanged(generation);
        long transactionGeneration = checked(generation + 1);
        IReadOnlyList<AddedProjectAsset> added;
        try
        {
            added = workspace.ApplyAssetTransaction(additions, allocated =>
            {
                Unchanged(transactionGeneration);
                var fresh = InvestmentVendorAuthoring.ReadBudget(catalog, sources);
                InvestmentVendorAuthoring.ValidateBudget(fresh, clone ? 1 : 0,
                    sales.Count - (clone ? 0 : detail!.Sales.Count),
                    clone ? BaseCategoryCount() : 0);
                Unchanged(transactionGeneration);
                if (allocated.Count != expected.Count || allocated.Where((asset, i) => asset.Native is not { } native
                    || TagOf(native.PackageId, native.EntryIndex ?? -1) != expected[i]).Any())
                    throw new InvalidDataException("Vendor allocations changed. Retry the operation.");
                return new(edits);
            }, clone ? $"Created {title}" : $"Edited {title}");
        }
        catch
        {
            if (ContextCurrent() && catalog.ReadGeneration == transactionGeneration) retryGeneration = transactionGeneration;
            throw;
        }
        return clone ? new(AddedAssetId: added[0].Id, Page: "sunrise.investment", Status: $"Created {title}")
            : new(Tag: selected.Tag, Page: "sunrise.investment", Status: $"Edited {title}");
    }
}
