namespace LadybugDb.Client.Cypher;

/// <summary>
/// Validates an engine type name before it is rendered verbatim - in DDL (<c>name INT64</c>) or a
/// <c>cast(x, 'INT64')</c>. Like <see cref="Identifier"/>, this is text the renderer interpolates,
/// so what it accepts is deliberately narrow.
/// </summary>
public static class TypeName
{
    /// <summary>
    /// Whether <paramref name="type"/> has the shape of an engine type name: a plain identifier
    /// (<c>INT64</c>, <c>STRING</c>, <c>TIMESTAMP_TZ</c>), optionally with a parenthesized
    /// comma-separated digit list (<c>DECIMAL(38, 10)</c>) and optionally a list or array suffix
    /// (<c>STRING[]</c>, <c>INT64[3]</c>). Nothing else - in particular no space outside the
    /// parentheses, no quote, no semicolon - so a type is a single token to the parser and cannot
    /// smuggle a second statement into the text it is interpolated into.
    /// </summary>
    /// <param name="type">The type name to test.</param>
    public static bool IsValid(string type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        var i = 0;
        while (i < type.Length && (char.IsAsciiLetterOrDigit(type[i]) || type[i] == '_')) i++;
        if (i == 0) return false;

        if (i < type.Length && type[i] == '(')
        {
            var close = type.IndexOf(')', i);
            if (close < 0) return false;
            var inner = type.AsSpan(i + 1, close - i - 1);
            if (inner.IsEmpty) return false;
            foreach (var part in inner.Split(','))
            {
                var digits = inner[part].Trim();
                if (digits.IsEmpty) return false;
                foreach (var c in digits)
                {
                    if (!char.IsAsciiDigit(c)) return false;
                }
            }

            i = close + 1;
        }

        if (i < type.Length && type[i] == '[')
        {
            var close = type.IndexOf(']', i);
            if (close < 0) return false;
            foreach (var c in type.AsSpan(i + 1, close - i - 1))
            {
                if (!char.IsAsciiDigit(c)) return false;
            }

            i = close + 1;
        }

        return i == type.Length;
    }
}
