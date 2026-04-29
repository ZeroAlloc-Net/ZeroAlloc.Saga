namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// Small string helpers shared by the emitters.
/// </summary>
internal static class NameUtil
{
    /// <summary>
    /// Returns the simple type name from a fully qualified name
    /// (last segment after the rightmost '.').
    /// </summary>
    public static string SimpleName(string fqn)
    {
        var idx = fqn.LastIndexOf('.');
        return idx >= 0 ? fqn.Substring(idx + 1) : fqn;
    }
}
