namespace InitiativeMerger.Web;

/// <summary>
/// Holds the current initiative JSON (after any filters/aliases/parameter edits)
/// so the download controller can serve it without passing large strings via SignalR.
/// </summary>
public static class DownloadCache
{
    private static string? _currentJson;
    private static string? _initiativeName;

    public static void Store(string json, string initiativeName)
    {
        _currentJson   = json;
        _initiativeName = initiativeName;
    }

    public static (string? Json, string? Name) Get() => (_currentJson, _initiativeName);
}
