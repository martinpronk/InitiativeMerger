using InitiativeMerger.Core.Models;

namespace InitiativeMerger.Web;

/// <summary>
/// Simple in-memory cache for passing a MergeResult between Blazor pages.
/// In a multi-user production environment: replace with IMemoryCache with a session key.
/// </summary>
public static class MergeResultCache
{
    private static MergeResult? _last;

    /// <summary>Stores the most recent merge result.</summary>
    public static void Store(MergeResult result) => _last = result;

    /// <summary>Retrieves the most recent merge result.</summary>
    public static MergeResult? Get() => _last;
}
