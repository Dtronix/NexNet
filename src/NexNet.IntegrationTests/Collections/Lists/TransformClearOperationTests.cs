using NexNet.Internals.Collections.Lists;
using NexNet.Internals.Collections.Versioned;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests.Collections.Lists;
public class TransformClearOperationTests
{
    [Test]
    public async Task SingleOperation_Single()
    {
        var txOp = new ClearOperation<int>();

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>().FillNegative(1);
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(Array.Empty<int>());

        }
    }
    
    [Test]
    public async Task Operation_ClearsBackHistory()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history1 = new InsertOperation<int>(0 ,1);
        var history2 = new InsertOperation<int>(1 ,2);
        var exOp = new ClearOperation<int>();

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>();
            list.ProcessOperation(history1, 0, out _);
            list.ProcessOperation(history2, 1, out _);
            list.ProcessOperation(exOp, 2, out _);
            await Assert.That(list.MinValidVersion).IsEqualTo(2);

        }
    }
    
    [Test]
    public async Task Operation_ExecutesTwice()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var exOp = new ClearOperation<int>();
        var exOp2 = new ClearOperation<int>();

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>();
            list.ProcessOperation(exOp, 0, out _);
            await Assert.That(list.MinValidVersion).IsEqualTo(0);
            await Assert.That(list.Count).IsEqualTo(0);
            list.ProcessOperation(exOp2, 1, out _);
            await Assert.That(list.MinValidVersion).IsEqualTo(1);
            await Assert.That(list.Count).IsEqualTo(0);

        }
    }
    
    [Test]
    public async Task Operation_RemoveAfterNoop()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new ClearOperation<int>();
        var txOp = new RemoveOperation<int>(1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsFalse();

            var list = new VersionedList<int>();
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out var result)).IsEqualTo(NoopOperation<int>.Instance);
            await Assert.That(result).IsEqualTo(ListProcessResult.DiscardOperation);
            await Assert.That(list).IsEqualTo(Array.Empty<int>());

        }
    }

    [Test]
    public async Task Operation_MoveAfterNoop()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new ClearOperation<int>();
        var txOp = new MoveOperation<int>(0, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsFalse();

            var list = new VersionedList<int>();
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out var result)).IsEqualTo(NoopOperation<int>.Instance);
            await Assert.That(result).IsEqualTo(ListProcessResult.DiscardOperation);
            await Assert.That(list).IsEqualTo(Array.Empty<int>());

        }
    }
    
    [Test]
    public async Task Operation_ModifyAfterNoop()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new ClearOperation<int>();
        var txOp = new ModifyOperation<int>(0, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsFalse();

            var list = new VersionedList<int>();
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out var result)).IsEqualTo(NoopOperation<int>.Instance);
            await Assert.That(result).IsEqualTo(ListProcessResult.DiscardOperation);
            await Assert.That(list).IsEqualTo(Array.Empty<int>());

        }
    }

    [Test]
    public async Task Operation_InsertAfter_PrependsInsert()
    {
        var history = new ClearOperation<int>();
        var txOp1 = new InsertOperation<int>(241, 1);
        var txOp2 = new InsertOperation<int>(21, 2);
        var txOp3 = new InsertOperation<int>(300, 3);
        var txOp4 = new InsertOperation<int>(3, 4);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>();
            list.ProcessOperation(history, 0, out _);
            var operation1 = list.ProcessOperation(txOp1, 0, out var opResult1);
            await Assert.That(operation1).IsNotNull();
            await Assert.That(opResult1).IsEqualTo(ListProcessResult.Successful);

            var operation2 = list.ProcessOperation(txOp2, 0, out var opResult2);
            await Assert.That(operation2).IsNotNull();
            await Assert.That(opResult2).IsEqualTo(ListProcessResult.Successful);

            var operation3 = list.ProcessOperation(txOp3, 0, out var opResult3);
            await Assert.That(operation3).IsNotNull();
            await Assert.That(opResult3).IsEqualTo(ListProcessResult.Successful);


            // This last item should be discarded since it's base version should account for the clear.
            var operation4 = list.ProcessOperation(txOp4, 1, out var opResult4);
            await Assert.That(operation4).IsNull();
            await Assert.That(opResult4).IsEqualTo(ListProcessResult.BadOperation);
            await Assert.That(list).IsEqualTo(new[] { 1, 2, 3 });

        }
    }
}