using NexNet.Internals.Collections.Lists;
using NexNet.Internals.Collections.Versioned;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections.Lists;

[TestFixture]
public class TransformClearOperationTests
{
    [Test]
    public void SingleOperation_Single()
    {
        var txOp = new ClearOperation<int>();

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(1);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(Array.Empty<int>()));
        });
    }
    
    [Test]
    public void Operation_ClearsBackHistory()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history1 = new InsertOperation<int>(0 ,1);
        var history2 = new InsertOperation<int>(1 ,2);
        var exOp = new ClearOperation<int>();

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>();
            list.ProcessOperation(history1, 0, out _);
            list.ProcessOperation(history2, 1, out _);
            list.ProcessOperation(exOp, 2, out _);
            Assert.That(list.MinValidVersion, Is.EqualTo(2));
        });
    }
    
    [Test]
    public void Operation_ExecutesTwice()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var exOp = new ClearOperation<int>();
        var exOp2 = new ClearOperation<int>();

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>();
            list.ProcessOperation(exOp, 0, out _);
            Assert.That(list.MinValidVersion, Is.EqualTo(0));
            Assert.That(list.Count, Is.EqualTo(0));
            list.ProcessOperation(exOp2, 1, out _);
            Assert.That(list.MinValidVersion, Is.EqualTo(1));
            Assert.That(list.Count, Is.EqualTo(0));
        });
    }
    
    [Test]
    public void Operation_RemoveAfterNoop()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new ClearOperation<int>();
        var txOp = new RemoveOperation<int>(1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.False);

            var list = new VersionedList<int>();
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out var result), Is.EqualTo(NoopOperation<int>.Instance));
            Assert.That(result, Is.EqualTo(ListProcessResult.DiscardOperation));
            Assert.That(list, Is.EqualTo(Array.Empty<int>()));
        });
    }

    [Test]
    public void Operation_MoveAfterNoop()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new ClearOperation<int>();
        var txOp = new MoveOperation<int>(0, 1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.False);

            var list = new VersionedList<int>();
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out var result), Is.EqualTo(NoopOperation<int>.Instance));
            Assert.That(result, Is.EqualTo(ListProcessResult.DiscardOperation));
            Assert.That(list, Is.EqualTo(Array.Empty<int>()));
        });
    }
    
    [Test]
    public void Operation_ModifyAfterNoop()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new ClearOperation<int>();
        var txOp = new ModifyOperation<int>(0, 1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.False);

            var list = new VersionedList<int>();
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out var result), Is.EqualTo(NoopOperation<int>.Instance));
            Assert.That(result, Is.EqualTo(ListProcessResult.DiscardOperation));
            Assert.That(list, Is.EqualTo(Array.Empty<int>()));
        });
    }

    [Test]
    public void Operation_InsertAfter_PrependsInsert()
    {
        var history = new ClearOperation<int>();
        var txOp1 = new InsertOperation<int>(241, 1);
        var txOp2 = new InsertOperation<int>(21, 2);
        var txOp3 = new InsertOperation<int>(300, 3);
        var txOp4 = new InsertOperation<int>(3, 4);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>();
            list.ProcessOperation(history, 0, out _);
            var operation1 = list.ProcessOperation(txOp1, 0, out var opResult1);
            Assert.That(operation1, Is.Not.Null);
            Assert.That(opResult1, Is.EqualTo(ListProcessResult.Successful));
            
            var operation2 = list.ProcessOperation(txOp2, 0, out var opResult2);
            Assert.That(operation2, Is.Not.Null);
            Assert.That(opResult2, Is.EqualTo(ListProcessResult.Successful));
            
            var operation3 = list.ProcessOperation(txOp3, 0, out var opResult3);
            Assert.That(operation3, Is.Not.Null);
            Assert.That(opResult3, Is.EqualTo(ListProcessResult.Successful));
            
            
            // This last item should be discarded since it's base version should account for the clear.
            var operation4 = list.ProcessOperation(txOp4, 1, out var opResult4);
            Assert.That(operation4, Is.Null);
            Assert.That(opResult4, Is.EqualTo(ListProcessResult.BadOperation));
            Assert.That(list, Is.EqualTo(new[] { 1, 2, 3 }));
        });
    }
}
