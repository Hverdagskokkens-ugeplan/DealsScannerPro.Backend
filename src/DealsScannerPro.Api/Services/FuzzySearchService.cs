namespace DealsScannerPro.Api.Services;

public class FuzzySearchService : IFuzzySearchService
{
    /// <summary>
    /// Calculate the Levenshtein distance (edit distance) between two strings.
    /// This represents the minimum number of single-character edits needed
    /// to transform one string into the other.
    /// </summary>
    public int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
        if (string.IsNullOrEmpty(t)) return s.Length;

        int n = s.Length, m = t.Length;
        var d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = char.ToLower(s[i - 1]) == char.ToLower(t[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost
                );
            }
        }

        return d[n, m];
    }

    /// <summary>
    /// Check if text contains a fuzzy match for the query.
    /// First tries exact substring match (fastest), then fuzzy match on individual words.
    /// </summary>
    /// <param name="text">The text to search in (e.g., product name)</param>
    /// <param name="query">The search query</param>
    /// <param name="threshold">Maximum Levenshtein distance for a match (default: 2)</param>
    public bool IsFuzzyMatch(string text, string query, int threshold = 2)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
            return false;

        // Fast path: exact substring match (case-insensitive)
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        // Fuzzy match: check each word in the text against the query
        var words = text.ToLower().Split(new[] { ' ', ',', '-', '/', '(', ')' },
            StringSplitOptions.RemoveEmptyEntries);
        var queryLower = query.ToLower().Trim();

        // Stricter matching for short queries to avoid false positives
        // e.g., "brød" should not match "brun" or "bord"
        var adjustedThreshold = queryLower.Length <= 4 ? 1 : threshold;

        return words.Any(word =>
            word.Length >= 2 && // Skip very short words
            char.ToLower(word[0]) == char.ToLower(queryLower[0]) && // First letter must match
            LevenshteinDistance(word, queryLower) <= adjustedThreshold
        );
    }
}
