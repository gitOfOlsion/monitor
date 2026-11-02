namespace PeripheralMonitor.Web.Services;

/// <summary>The public origin of the site (e.g. https://monitorhubs.net), without a trailing slash.</summary>
public static class SiteUrl
{
    public static string Base(IConfiguration config)
        => (config["PublicUrl"] is { Length: > 0 } url ? url : "https://monitorhubs.net").TrimEnd('/');
}
