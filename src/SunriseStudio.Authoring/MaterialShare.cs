namespace SunriseStudio.Authoring;

/**
 * Materials one import has prepared, by content. A material whose inputs match one already prepared
 * reuses its tags and adds no entries. New materials stay staged until the caller commits them.
 */
public sealed class MaterialShare
{
    private readonly Dictionary<string, OriginalAssetMaterials.Prepared> kept = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OriginalAssetMaterials.Prepared> staged = new(StringComparer.Ordinal);

    /** Materials reused instead of prepared again. */
    public int Reused { get; private set; }

    internal bool TryGet(string key, out OriginalAssetMaterials.Prepared prepared)
    {
        bool found = kept.TryGetValue(key, out prepared!) || staged.TryGetValue(key, out prepared!);
        if (found) Reused++;
        return found;
    }

    internal void Stage(string key, OriginalAssetMaterials.Prepared prepared) => staged[key] = prepared;

    /** Keeps the staged materials; call once their assets are part of the result. */
    public void Commit()
    {
        foreach (var (key, prepared) in staged) kept[key] = prepared;
        staged.Clear();
    }

    /** Forgets the staged materials; call when their assets are thrown away. */
    public void Discard() => staged.Clear();
}
