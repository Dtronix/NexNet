using System.Collections.Immutable;
using NexNet.Internals.Collections.Versioned;

namespace NexNet.IntegrationTests.Collections.Lists;
/// <summary>
/// Rebase behavior of <see cref="VersionedList{T}.ProcessOperation"/> across Clears and history wrap (F18).
/// </summary>
public class VersionedListRebaseTests
{
    [Test]
    public async Task ConcurrentInsertsAfterClear_AreRebased()
    {
        var list = new VersionedList<int>();
        await Assert.That(list.ProcessOperation(new InsertOperation<int>(0, 1), 0, out _)).IsNotNull();
        await Assert.That(list.ProcessOperation(new ClearOperation<int>(), 1, out _)).IsNotNull();
        await Assert.That(list.Version).IsEqualTo(2);

        await Assert.That(list.ProcessOperation(new InsertOperation<int>(0, 10), 2, out var r1)).IsNotNull();
        await Assert.That(r1).IsEqualTo(ListProcessResult.Successful);

        // Concurrent with the insert above: also based on version 2.
        await Assert.That(list.ProcessOperation(new InsertOperation<int>(0, 20), 2, out var r2)).IsNotNull();
        await Assert.That(r2).IsEqualTo(ListProcessResult.Successful);

        await Assert.That(list).IsEquivalentTo(new[] { 10, 20 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task InsertBasedBeforeClear_IsRebasedFromTheClear()
    {
        var list = new VersionedList<int>();
        list.ProcessOperation(new InsertOperation<int>(0, 1), 0, out _);
        list.ProcessOperation(new InsertOperation<int>(1, 2), 1, out _);
        list.ProcessOperation(new ClearOperation<int>(), 2, out _);
        list.ProcessOperation(new InsertOperation<int>(0, 10), 3, out _);

        // Based on version 1, before the Clear. It must not close the pipe; Insert survives a Clear at index 0
        // and is then transformed against the insert of 10.
        var op = list.ProcessOperation(new InsertOperation<int>(1, 20), 1, out var result);
        await Assert.That(result).IsEqualTo(ListProcessResult.Successful);
        await Assert.That(op).IsTypeOf<InsertOperation<int>>();
        await Assert.That(list).IsEquivalentTo(new[] { 10, 20 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RemoveBasedBeforeClear_IsDiscarded()
    {
        var list = new VersionedList<int>();
        list.ProcessOperation(new InsertOperation<int>(0, 1), 0, out _);
        list.ProcessOperation(new ClearOperation<int>(), 1, out _);
        list.ProcessOperation(new InsertOperation<int>(0, 10), 2, out _);

        var op = list.ProcessOperation(new RemoveOperation<int>(0), 1, out var result);
        await Assert.That(op).IsSameReferenceAs(NoopOperation<int>.Instance);
        await Assert.That(result).IsEqualTo(ListProcessResult.DiscardOperation);
        await Assert.That(list).IsEquivalentTo(new[] { 10 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ClearWithStaleBase_IsApplied()
    {
        var list = new VersionedList<int>(maxVersions: 4);
        for (var i = 0; i < 10; i++)
            list.ProcessOperation(new InsertOperation<int>(0, i), i, out _);

        var op = list.ProcessOperation(new ClearOperation<int>(), 0, out var result);
        await Assert.That(op).IsTypeOf<ClearOperation<int>>();
        await Assert.That(result).IsEqualTo(ListProcessResult.Successful);
        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(list.Version).IsEqualTo(11);
    }

    [Test]
    public async Task ConcurrentInsertsAfterHistoryWrap_AreRebased()
    {
        const int max = 8;
        var list = new VersionedList<int>(max);
        for (var i = 0; i < max * 3; i++)
            list.ProcessOperation(new InsertOperation<int>(i, i), i, out _);

        var baseVersion = list.Version;
        list.ProcessOperation(new InsertOperation<int>(0, 100), baseVersion, out _);
        list.ProcessOperation(new InsertOperation<int>(0, 200), baseVersion, out var result);

        await Assert.That(result).IsEqualTo(ListProcessResult.Successful);
        await Assert.That(list[0]).IsEqualTo(100);
        await Assert.That(list[1]).IsEqualTo(200);
    }

    [Test]
    public async Task RequestOlderThanHistory_ReturnsNoopAndLeavesListUntouched()
    {
        const int max = 8;
        var list = new VersionedList<int>(max);
        for (var i = 0; i < max + 2; i++)
            list.ProcessOperation(new InsertOperation<int>(i, i), i, out _);

        await Assert.That(list.MinValidVersion).IsEqualTo(2);
        var before = list.ToArray();
        var version = list.Version;

        var op = list.ProcessOperation(new InsertOperation<int>(0, 99), 1, out var result);
        await Assert.That(op).IsSameReferenceAs(NoopOperation<int>.Instance);
        await Assert.That(result).IsEqualTo(ListProcessResult.OutOfOperationalRange);
        await Assert.That(list.Version).IsEqualTo(version);
        await Assert.That(list).IsEquivalentTo(before, CollectionOrdering.Matching);

        // The oldest stored version is still rebasable.
        await Assert.That(list.ProcessOperation(new InsertOperation<int>(0, 99), 2, out result)).IsNotNull();
        await Assert.That(result).IsEqualTo(ListProcessResult.Successful);
    }

    [Test]
    public async Task ResetTo_AcceptsRequestsAtTheResetVersion()
    {
        var list = new VersionedList<int>();
        list.ResetTo(ImmutableList.Create(1, 2, 3), 50);

        list.ProcessOperation(new InsertOperation<int>(0, 10), 50, out var r1);
        list.ProcessOperation(new InsertOperation<int>(0, 20), 50, out var r2);
        await Assert.That(r1).IsEqualTo(ListProcessResult.Successful);
        await Assert.That(r2).IsEqualTo(ListProcessResult.Successful);
        await Assert.That(list).IsEquivalentTo(new[] { 10, 20, 1, 2, 3 }, CollectionOrdering.Matching);
        await Assert.That(list.Version).IsEqualTo(52);

        list.ProcessOperation(new InsertOperation<int>(0, 30), 49, out var r3);
        await Assert.That(r3).IsEqualTo(ListProcessResult.OutOfOperationalRange);
    }

    [Test]
    public async Task Reset_RestartsHistoryAtVersionZero()
    {
        var list = new VersionedList<int>();
        for (var i = 0; i < 5; i++)
            list.ProcessOperation(new InsertOperation<int>(0, i), i, out _);

        list.Reset();
        list.ProcessOperation(new InsertOperation<int>(0, 10), 0, out var r1);
        list.ProcessOperation(new InsertOperation<int>(0, 20), 0, out var r2);
        await Assert.That(r1).IsEqualTo(ListProcessResult.Successful);
        await Assert.That(r2).IsEqualTo(ListProcessResult.Successful);
        await Assert.That(list).IsEquivalentTo(new[] { 10, 20 }, CollectionOrdering.Matching);
    }

    // ---------------------------------------------------------------------------------------------
    // Property tests: random concurrent requests (lagging clients), including Clears and more changes
    // than the history holds, compared with a reference rebaser that keeps the full, unbounded history.
    // ---------------------------------------------------------------------------------------------

    [Test]
    [Arguments(1, 1024, 3000, false, 0.0)]
    [Arguments(2, 1024, 3000, true, 0.0)]
    [Arguments(3, 16, 4000, false, 0.01)]
    [Arguments(4, 16, 4000, true, 0.01)]
    [Arguments(5, 64, 4000, false, 0.005)]
    [Arguments(6, 64, 4000, true, 0.005)]
    public async Task RandomConcurrentOperations_MatchReferenceAndConverge(
        int seed, int maxVersions, int requests, bool withMoves, double clearRate)
    {
        var random = new Random(seed);
        var server = new VersionedList<int>(maxVersions);

        // Snapshot of the server list at each version, as a lagging client would hold it.
        var snapshots = new List<ImmutableList<int>> { server.State.List };

        // Reference: full history (entry v moves v -> v+1) and its own state.
        var refHistory = new List<Operation<int>>();
        var refState = new VersionedList<int>.ListState(ImmutableList<int>.Empty, 0);

        // Broadcast log: (rebased op, version after it), as clients receive it.
        var broadcasts = new List<(Operation<int> Op, int Version)>();

        var nextValue = 1;
        int applied = 0, discarded = 0, invalid = 0, outOfRange = 0, clears = 0, rebasedFromClear = 0;
        int sinceClear = 0, maxSinceClear = 0;
        // Forced Clears late in the run, after the history has had the chance to wrap.
        var forcedClears = new HashSet<int> { requests * 3 / 4, requests * 3 / 4 + 7 };

        for (var n = 0; n < requests; n++)
        {
            var version = server.Version;

            // Mostly recent bases, sometimes far behind (beyond the history).
            int lag = random.NextDouble() switch
            {
                < 0.70 => random.Next(0, Math.Min(4, version + 1)),
                < 0.95 => random.Next(0, Math.Min(maxVersions, version + 1)),
                _ => random.Next(0, Math.Min(maxVersions * 2, version + 1)),
            };
            var baseVersion = version - lag;
            var baseList = snapshots[baseVersion];
            var count = baseList.Count;

            // Choose an operation valid against the client's (stale) view.
            Operation<int> op;
            var roll = random.NextDouble();
            if (roll < clearRate || forcedClears.Contains(n))
            {
                op = new ClearOperation<int>();
            }
            else if (roll < 0.45 || count == 0)
            {
                op = new InsertOperation<int>(random.Next(0, count + 1), nextValue++);
            }
            else if (roll < 0.70)
            {
                op = new RemoveOperation<int>(random.Next(0, count));
            }
            else if (roll < 0.85 || !withMoves || count < 2)
            {
                op = new ModifyOperation<int>(random.Next(0, count), nextValue++);
            }
            else
            {
                var from = random.Next(0, count);
                int to;
                do { to = random.Next(0, count); } while (to == from);
                op = new MoveOperation<int>(from, to);
            }

            var reference = ReferenceRebase(op.Clone(), baseVersion, refHistory);
            var historyStart = server.MinValidVersion;
            var result = server.ProcessOperation(op, baseVersion, out var processResult);

            if (processResult == ListProcessResult.OutOfOperationalRange)
            {
                // Only possible when the base is older than the stored history and no Clear covers it.
                await Assert.That((long)baseVersion).IsLessThan((long)historyStart).Because($"Request {n}: in-range base {baseVersion} rejected");
                await Assert.That(result).IsSameReferenceAs(NoopOperation<int>.Instance);
                await Assert.That(server.Version).IsEqualTo(version);
                outOfRange++;
                continue;
            }

            if (baseVersion < historyStart)
                rebasedFromClear++;

            // In range (or covered by a Clear): the server must decide exactly as the reference does.
            if (reference is null)
            {
                await Assert.That(result).IsSameReferenceAs(NoopOperation<int>.Instance).Because($"Request {n}: expected discard");
                await Assert.That(processResult).IsEqualTo(ListProcessResult.DiscardOperation);
                discarded++;
                continue;
            }

            if (!IsValid(reference, refState.List.Count))
            {
                // A transform can make a request invalid (e.g. a Move onto itself); the server rejects it.
                await Assert.That(result).IsNull().Because($"Request {n}: expected rejection");
                await Assert.That(processResult).IsEqualTo(ListProcessResult.BadOperation);
                await Assert.That(server.Version).IsEqualTo(version);
                invalid++;
                continue;
            }

            await Assert.That(processResult).IsEqualTo(ListProcessResult.Successful).Because($"Request {n}: {op.GetType().Name} based on {baseVersion} (server {version}) failed");
            await Assert.That(result).IsEqualTo(reference).Because($"Request {n}: rebased operation differs from reference");

            reference.Apply(ref refState);
            refHistory.Add(reference.Clone());
            if (reference is ClearOperation<int>)
            {
                clears++;
                sinceClear = 0;
            }
            else
            {
                maxSinceClear = Math.Max(maxSinceClear, ++sinceClear);
            }

            await Assert.That(server.Version).IsEqualTo(version + 1);
            await Assert.That(refState.Version).IsEqualTo(server.Version);
            await Assert.That(server).IsEquivalentTo(refState.List.ToArray(), CollectionOrdering.Matching).Because($"Request {n}: server diverged from reference");

            broadcasts.Add((result!.Clone(), server.Version));
            snapshots.Add(server.State.List);
            applied++;
        }

        // A client replica applying the broadcast stream serially converges to the server.
        var replica = new VersionedList<int>(maxVersions);
        foreach (var (bop, bversion) in broadcasts)
            await Assert.That(replica.ApplyOperation(bop, bversion)).IsEqualTo(ListProcessResult.Successful);

        await Assert.That(replica.Version).IsEqualTo(server.Version);
        await Assert.That(replica).IsEquivalentTo(server.State.List.ToArray(), CollectionOrdering.Matching);

        // Make sure the scenario actually exercised what it is meant to.
        await Assert.That(maxSinceClear).IsGreaterThan(maxVersions).Because("history never wrapped");
        await Assert.That(outOfRange).IsGreaterThan(0).Because("no request fell out of the history");
        await Assert.That(rebasedFromClear).IsGreaterThan(0).Because("no request based before a Clear was rebased");
        await Assert.That(clears).IsGreaterThan(0).Because("no Clear applied");
        await Assert.That(discarded).IsGreaterThan(0).Because("no request discarded");
        Console.Out.WriteLine(
            $"applied={applied} discarded={discarded} invalid={invalid} outOfRange={outOfRange} clears={clears} rebasedFromClear={rebasedFromClear}");
    }

    /// <summary>
    /// Rebases <paramref name="op"/> against every history entry after <paramref name="baseVersion"/> using
    /// an unbounded history. Returns null when a transform discards the request.
    /// </summary>
    private static Operation<int>? ReferenceRebase(Operation<int> op, int baseVersion, List<Operation<int>> history)
    {
        if (op is ClearOperation<int>)
            return op;

        for (var i = baseVersion; i < history.Count; i++)
        {
            if (!op.TransformAgainst(history[i]))
                return null;
        }

        return op;
    }

    private static bool IsValid(Operation<int> op, int count) => op switch
    {
        InsertOperation<int> o => o.Index >= 0 && o.Index <= count,
        RemoveOperation<int> o => o.Index >= 0 && o.Index < count,
        ModifyOperation<int> o => o.Index >= 0 && o.Index < count,
        MoveOperation<int> o => o.FromIndex >= 0 && o.FromIndex < count && o.ToIndex >= 0 && o.ToIndex < count
                                && o.FromIndex != o.ToIndex,
        _ => true,
    };
}