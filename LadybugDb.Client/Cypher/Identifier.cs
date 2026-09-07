namespace LadybugDb.Client.Cypher;

/// <summary>
/// Renders a schema identifier - a table, property, variable, or alias name - the way the engine's
/// parser needs to see it: verbatim when it is a plain identifier, backtick-quoted otherwise.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place the renderer ever interpolates caller-supplied text into Cypher, and it
/// is safe to do so because an identifier is not a value: what it can contain is decided by the
/// schema, and the quoting below (a backtick inside a name is doubled, the whole name wrapped in
/// backticks) makes every string a single token to the parser. Values never take this path - they
/// render as <c>$p&lt;n&gt;</c> parameters and are bound, never interpolated.
/// </para>
/// <para>
/// "Plain" is deliberately narrow: ASCII letters, digits and underscores, not starting with a digit.
/// Anything else - a space, a hyphen, a non-ASCII letter the engine may or may not accept bare - is
/// backticked, which is always legal. Measured against the engine: <c>MATCH (`o`:`Object`) RETURN
/// `o`.`name` AS `my name`</c> runs, and the column comes back named <c>my name</c>.
/// </para>
/// <para>
/// A plain name that is one of the parser's reserved words is backticked too: <c>RETURN x AS End</c>
/// is a parse error (measured: "mismatched input 'End'"), <c>AS `End`</c> runs and the column
/// comes back named <c>End</c>. The list is every keyword the engine's grammar does not also accept
/// as a symbolic name, so a projection named after a C# property such as <c>End</c>, <c>Order</c>
/// or <c>Any</c> renders rather than fails; over-quoting a name the parser would have accepted
/// bare costs nothing.
/// </para>
/// </remarks>
public static class Identifier
{
    /// <summary>
    /// Renders <paramref name="name"/> as a Cypher identifier: verbatim if it is a plain identifier
    /// (see <see cref="IsPlain"/>), otherwise backtick-quoted with any embedded backtick doubled.
    /// </summary>
    /// <param name="name">The identifier.</param>
    /// <returns>The identifier as it appears in Cypher text.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public static string Render(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return IsPlain(name) && !IsReserved(name) ? name : "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    /// <summary>Whether <paramref name="name"/> is a reserved word of the engine's Cypher grammar (case-insensitively), which must be backticked to name anything.</summary>
    /// <param name="name">The identifier to test.</param>
    public static bool IsReserved(string name) => Reserved.Contains(name);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALL", "AND", "ANY", "ASC", "ASCENDING", "CASE", "CAST", "COLUMN", "CREATE", "DBTYPE", "DEFAULT",
        "DESC", "DESCENDING", "DISTINCT", "ELSE", "END", "ENDS", "EXISTS", "FALSE", "GLOB", "GROUP",
        "HEADERS", "HINT", "IN", "INSTALL", "JOIN", "MACRO", "MULTI_JOIN", "NONE", "NOT", "NULL", "ON",
        "ONLY", "OPTIONAL", "OR", "ORDER", "PRIMARY", "PROFILE", "RDFGRAPH", "SHORTEST", "SINGLE",
        "STARTS", "TABLE", "THEN", "TRUE", "UNION", "UNWIND", "WHEN", "WHERE", "WITH", "XOR",
    };

    /// <summary>
    /// Whether <paramref name="name"/> can appear in Cypher without quoting: ASCII letters, digits
    /// and underscores only, and not starting with a digit.
    /// </summary>
    /// <param name="name">The identifier to test.</param>
    public static bool IsPlain(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        }

        return true;
    }
}
