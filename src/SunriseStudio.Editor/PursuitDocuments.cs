using System.Runtime.CompilerServices;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/** Which pursuit page an item document opens on, parsed once per document since CanOpen runs every frame. */
internal static class PursuitDocuments
{
    private sealed record Box((InvestmentPursuitShape Shape, byte Bucket) Value);
    // Reopening a document replaces its record, so each reference keys exactly one snapshot of its bytes.
    private static readonly ConditionalWeakTable<StudioDocument, Box> Shapes = new();

    /** The pursuit shape and bucket of an item definition. Any other document is None in bucket 255, which means no bucket. */
    public static (InvestmentPursuitShape Shape, byte Bucket) Of(StudioDocument document)
    {
        if (document.Entry.Reference != InvestmentCatalog.ItemClass
            || document.Entry.TagHash is InvestmentCatalog.ItemIndexTag or InvestmentCatalog.DisplayIndexTag)
            return (InvestmentPursuitShape.None, byte.MaxValue);
        return Shapes.GetValue(document, shown => new(InvestmentPursuits.Classify(shown.Bytes))).Value;
    }

    /** The Quest or Pursuit page id that shows this document's objectives, or null when the Investment page keeps them. */
    public static string? Page(StudioDocument document) => Of(document) switch
    {
        (InvestmentPursuitShape.Quest, _) => QuestPage.PageId,
        (InvestmentPursuitShape.Single, InvestmentPursuits.PursuitBucket) => PursuitPage.PageId,
        _ => null
    };
}
