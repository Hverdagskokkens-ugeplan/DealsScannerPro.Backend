namespace DealsScannerPro.Api.Services;

public interface IFuzzySearchService
{
    /// <summary>
    /// Calculate Levenshtein distance between two strings.
    /// </summary>
    int LevenshteinDistance(string s, string t);

    /// <summary>
    /// Check if text contains a fuzzy match for the query.
    /// First tries exact substring match, then fuzzy match on individual words.
    /// </summary>
    bool IsFuzzyMatch(string text, string query, int threshold = 2);
}
