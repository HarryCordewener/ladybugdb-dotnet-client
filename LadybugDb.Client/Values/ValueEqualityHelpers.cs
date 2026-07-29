namespace LadybugDb.Client;

/// <summary>
/// Shared structural equality/hashing helpers for the property dictionaries
/// <see cref="LadybugValue"/> (its <c>STRUCT</c> payload), <see cref="LadybugNode"/>, and
/// <see cref="LadybugRel"/> all carry (<c>IReadOnlyDictionary&lt;string, LadybugValue&gt;</c>).
/// Factored out once rather than duplicated three times: a dictionary has no inherent order, so
/// both equality and hashing here are deliberately order-independent - two property bags with the
/// same keys and values but populated in a different order (e.g. the engine changing field
/// enumeration order between calls, which this client makes no promise about) must still compare
/// and hash equal.
/// </summary>
internal static class ValueEqualityHelpers
{
    internal static bool DictionaryEquals(
        IReadOnlyDictionary<string, LadybugValue> a, IReadOnlyDictionary<string, LadybugValue> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var otherValue) || !value.Equals(otherValue)) return false;
        }
        return true;
    }

    internal static int DictionaryHashCode(IReadOnlyDictionary<string, LadybugValue> dictionary)
    {
        // XOR combine, not HashCode.Combine/sequential Add: those are order-sensitive, which
        // would make this inconsistent with DictionaryEquals's order-independence above (equal
        // dictionaries must hash equal regardless of enumeration order).
        var hash = 0;
        foreach (var (key, value) in dictionary)
            hash ^= HashCode.Combine(key, value);
        return hash;
    }

    internal static string DescribeDictionary(IReadOnlyDictionary<string, LadybugValue> dictionary) =>
        "{" + string.Join(", ", dictionary.Select(kv => $"{kv.Key}: {kv.Value}")) + "}";
}
