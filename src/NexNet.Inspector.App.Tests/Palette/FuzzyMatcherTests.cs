using NexNet.Inspector.App.Model.Palette;

namespace NexNet.Inspector.App.Tests.Palette;

/// <summary>The palette's fuzzy ranking: substrings over subsequences, whole words and ids first, no allocation.</summary>
public sealed class FuzzyMatcherTests
{
    [Test]
    public async Task An_empty_query_matches_everything_and_a_missing_letter_matches_nothing()
    {
        await Assert.That(FuzzyMatcher.Score("", "Invocation #812")).IsEqualTo(0);
        await Assert.That(FuzzyMatcher.Score("   ", "Invocation #812")).IsEqualTo(0);
        await Assert.That(FuzzyMatcher.Score("xyz", "Invocation #812")).IsEqualTo(FuzzyMatcher.NoMatch);
        await Assert.That(FuzzyMatcher.Score("Invocation #8123", "Invocation #812")).IsEqualTo(FuzzyMatcher.NoMatch);
    }

    [Test]
    public async Task A_whole_id_outranks_the_same_digits_starting_a_longer_number()
    {
        int exact = FuzzyMatcher.Score("812", "Invocation #812 SendAttachment");
        int longer = FuzzyMatcher.Score("812", "Invocation #8120 SendMessage");
        int inside = FuzzyMatcher.Score("812", "Invocation #18120 Join");
        int pipe = FuzzyMatcher.Score("812", "Chan 812·1 LogLine");

        await Assert.That(exact).IsGreaterThan(longer);
        await Assert.That(longer).IsGreaterThan(inside);
        await Assert.That(exact).IsGreaterThan(pipe);
    }

    [Test]
    public async Task Substrings_outrank_subsequences_and_word_starts_outrank_letters_inside_words()
    {
        int substring = FuzzyMatcher.Score("send", "Invocation #4 SendMessage");
        int subsequence = FuzzyMatcher.Score("sdmg", "Invocation #4 SendMessage");
        int hump = FuzzyMatcher.Score("att", "Invocation #4 SendAttachment");
        int inside = FuzzyMatcher.Score("att", "Invocation #4 Chattering");

        await Assert.That(subsequence).IsNotEqualTo(FuzzyMatcher.NoMatch);
        await Assert.That(substring).IsGreaterThan(subsequence);
        await Assert.That(hump).IsGreaterThan(inside);
    }

    [Test]
    public async Task Matching_ignores_case_and_every_word_of_the_query_must_match()
    {
        await Assert.That(FuzzyMatcher.Score("SENDATT", "SendAttachment")).IsEqualTo(FuzzyMatcher.Score("sendatt", "SendAttachment"));
        await Assert.That(FuzzyMatcher.Score("send att", "Invocation #4 SendAttachment")).IsNotEqualTo(FuzzyMatcher.NoMatch);
        await Assert.That(FuzzyMatcher.Score("send zzz", "Invocation #4 SendAttachment")).IsEqualTo(FuzzyMatcher.NoMatch);
    }

    [Test]
    public async Task Among_equal_matches_the_shorter_text_ranks_first()
    {
        await Assert.That(FuzzyMatcher.Score("theme", "Theme: Dark")).IsGreaterThan(FuzzyMatcher.Score("theme", "Theme: System default"));
    }

    [Test]
    public async Task Scoring_allocates_nothing()
    {
        const string text = "#18201 MessageChunk inv 812 SendAttachment";
        FuzzyMatcher.Score("msgch 812", text);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            sum += FuzzyMatcher.Score("812", text) & 1;
            sum += FuzzyMatcher.Score("msgch 812", text) & 1;
            sum += FuzzyMatcher.Score("zz", text) & 1;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0L);
        await Assert.That(sum).IsGreaterThanOrEqualTo(0);
    }
}
