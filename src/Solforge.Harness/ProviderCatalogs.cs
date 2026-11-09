namespace Solforge.Harness;

/** Retired provider detection for legacy settings. No retired catalogs are offered. */
public static class ProviderCatalogs
{
    /** Whether a legacy entry uses the retired Cline endpoint. */
    public static bool IsClineUsageBilled(HarnessModel model) =>
        Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var uri) && uri.Host.Equals("api.cline.bot", StringComparison.OrdinalIgnoreCase);
}
