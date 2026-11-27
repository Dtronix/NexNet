using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.Tests.Compare;

/// <summary>Entity correlation: the same calls pair up, an inserted call stays alone, a changed call pairs in place.</summary>
public class SequenceCorrelatorTests
{
    private static List<(int A, int B)> Correlate(ulong[] a, ulong[] b, int maxCells = 1 << 20)
    {
        var pairs = new List<(int A, int B)>();
        SequenceCorrelator.Correlate(a, b, maxCells, pairs);
        return pairs;
    }

    private static string Text(List<(int A, int B)> pairs) => string.Join(" ", pairs.Select(p => $"({p.A},{p.B})"));

    [Test]
    public async Task Identical_sequences_pair_one_to_one()
    {
        var pairs = Correlate([1, 2, 3, 4], [1, 2, 3, 4]);
        await Assert.That(Text(pairs)).IsEqualTo("(0,0) (1,1) (2,2) (3,3)");
    }

    [Test]
    public async Task An_insertion_stays_alone_and_a_change_pairs_in_place()
    {
        // The variant fixture: the first message changed, one inserted after it, the rest equal.
        var pairs = Correlate([10, 20, 30, 40], [11, 99, 20, 30, 40]);
        await Assert.That(Text(pairs)).IsEqualTo("(0,0) (-1,1) (1,2) (2,3) (3,4)");
    }

    [Test]
    public async Task A_deletion_in_the_middle_keeps_the_rest_paired()
    {
        var pairs = Correlate([1, 2, 3, 4, 5], [1, 2, 4, 5]);
        await Assert.That(Text(pairs)).IsEqualTo("(0,0) (1,1) (2,-1) (3,2) (4,3)");
    }

    [Test]
    public async Task Common_elements_found_out_of_the_head_and_tail()
    {
        var pairs = Correlate([7, 1, 2, 8, 3], [9, 1, 2, 3, 6]);
        await Assert.That(Text(pairs)).IsEqualTo("(0,0) (1,1) (2,2) (3,-1) (4,3) (-1,4)");
    }

    [Test]
    public async Task Beyond_the_budget_the_middle_pairs_by_position()
    {
        // Exactly, 5 would stay alone and 6, 7 pair with 6, 7; over the budget the middle pairs in order.
        var exact = Correlate([1, 5, 6, 7, 9, 2], [1, 6, 7, 8, 2]);
        await Assert.That(Text(exact)).IsEqualTo("(0,0) (1,-1) (2,1) (3,2) (4,3) (5,4)");
        var pairs = Correlate([1, 5, 6, 7, 9, 2], [1, 6, 7, 8, 2], maxCells: 2);
        await Assert.That(Text(pairs)).IsEqualTo("(0,0) (1,1) (2,2) (3,3) (4,-1) (5,4)");
    }

    [Test]
    public async Task Empty_sides()
    {
        await Assert.That(Text(Correlate([], [1, 2]))).IsEqualTo("(-1,0) (-1,1)");
        await Assert.That(Text(Correlate([1], []))).IsEqualTo("(0,-1)");
        await Assert.That(Correlate([], [])).IsEmpty();
    }
}
