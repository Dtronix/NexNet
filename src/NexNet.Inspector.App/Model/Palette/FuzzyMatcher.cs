namespace NexNet.Inspector.App.Model.Palette;

/// <summary>
/// Scores a palette query against a candidate's text, without allocating: every word of the query must appear in the
/// text, as a substring (best) or as a subsequence (letters in order with gaps). Higher is better; a candidate that
/// does not match scores <see cref="NoMatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// A substring always outranks a subsequence. Among substrings, one that is a whole word (bounded on both sides by
/// a non-alphanumeric character, a camel-case hump or the text's edge) outranks one that only starts a word, which
/// outranks one inside a word; one right after a '#' (an id, as the app writes them) gets a bonus; earlier is better.
/// So "812" ranks "Invocation #812" over "Invocation #8120", and over "Chan 812·1".
/// </para>
/// <para>
/// A subsequence earns points per letter at a word start and per letter that follows the previous one, and loses
/// points for the letters skipped between them (greedy, leftmost: good enough for short palette labels).
/// </para>
/// </remarks>
public static class FuzzyMatcher
{
    /// <summary>The score of a candidate that does not match.</summary>
    public const int NoMatch = int.MinValue;

    // Substring scores start above any subsequence score.
    private const int SubstringBase = 10_000;
    private const int SubsequenceBase = 1_000;
    private const int SubsequenceMax = SubstringBase - 1;

    // A '#' then digits is how the app writes an id (#812): a query matching right after a '#' names that thing, not a number in passing.
    private const int IdBonus = 800;

    /// <summary>Scores <paramref name="text"/> for <paramref name="query"/>; an empty query matches everything with 0.</summary>
    public static int Score(ReadOnlySpan<char> query, ReadOnlySpan<char> text)
    {
        int total = 0;
        bool any = false;
        while (true)
        {
            query = query.TrimStart(' ');
            if (query.IsEmpty) break;
            int end = query.IndexOf(' ');
            ReadOnlySpan<char> word = end < 0 ? query : query[..end];
            query = end < 0 ? default : query[end..];

            int score = ScoreWord(word, text);
            if (score == NoMatch) return NoMatch;
            total += score;
            any = true;
        }

        // Among equal matches, the shorter text is the closer one.
        return any ? total - Math.Min(text.Length, 200) : 0;
    }

    /// <summary>Scores one word of a query (no spaces).</summary>
    public static int ScoreWord(ReadOnlySpan<char> word, ReadOnlySpan<char> text)
    {
        if (word.IsEmpty) return 0;
        if (word.Length > text.Length) return NoMatch;

        int best = NoMatch;
        int from = 0;
        while (from <= text.Length - word.Length)
        {
            int at = text[from..].IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            at += from;
            int score = SubstringBase + (StartsWord(text, at) ? 2_000 : 0) + (EndsWord(text, at + word.Length) ? 1_000 : 0)
                        + (at == 0 ? 500 : 0) + (at > 0 && text[at - 1] == '#' ? IdBonus : 0) - Math.Min(at, 400);
            if (score > best) best = score;
            from = at + 1;
        }

        return best != NoMatch ? best : Subsequence(word, text);
    }

    private static int Subsequence(ReadOnlySpan<char> word, ReadOnlySpan<char> text)
    {
        int score = SubsequenceBase;
        int position = 0;
        int previous = -2;
        foreach (char c in word)
        {
            char lower = char.ToLowerInvariant(c);
            int found = -1;
            for (int i = position; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == lower)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0) return NoMatch;
            if (found == previous + 1) score += 60;
            else score -= Math.Min(found - position, 40);
            if (StartsWord(text, found)) score += 80;
            previous = found;
            position = found + 1;
        }

        return Math.Min(score, SubsequenceMax);
    }

    private static bool StartsWord(ReadOnlySpan<char> text, int at)
    {
        if (at == 0) return true;
        char before = text[at - 1], c = text[at];
        if (!char.IsLetterOrDigit(before)) return true;
        // A camel-case hump (SendAttachment's A) or a letter/digit change (Pipe3).
        return (char.IsUpper(c) && char.IsLower(before)) || char.IsDigit(c) != char.IsDigit(before);
    }

    private static bool EndsWord(ReadOnlySpan<char> text, int end) => end >= text.Length || StartsWord(text, end);
}
