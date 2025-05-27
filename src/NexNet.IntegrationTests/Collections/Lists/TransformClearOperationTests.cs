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
            list.ProcessOperation(exOp2, 1, out _);
            Assert.That(list.MinValidVersion, Is.EqualTo(1));
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
    public void Operation_InsertAfter_AppendsInsert()
    {
        var history = new ClearOperation<int>();
        var txOp1 = new InsertOperation<int>(241, 1);
        var txOp2 = new InsertOperation<int>(21, 2);
        var txOp3 = new InsertOperation<int>(300, 3);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>();
            list.ProcessOperation(history, 0, out _);
            var operation1 = list.ProcessOperation(txOp1, 0, out _);
            var operation2 = list.ProcessOperation(txOp2, 2, out _);
            var operation3 = list.ProcessOperation(txOp3, 3, out _);
            Assert.That(list, Is.EqualTo(new[] { 1, 2, 3 }));
        });
    }

    [Test]
    public void OperationAfterRemove_Before_ShiftsIndexDown()
    {
        var history = new RemoveOperation<int>(0);
        var txOp = new RemoveOperation<int>(1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(0));
            
            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(Array.Empty<int>()));
        });
    }

    [Test]
    public void OperationAfterRemove_After_Noop()
    {
        var history = new RemoveOperation<int>(1);
        var txOp = new RemoveOperation<int>(1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.False);

            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.TypeOf<NoopOperation<int>>());
            Assert.That(list, Is.EqualTo(new[] { -1 }));
        });
    }

    [Test]
    public void OperationAfterMove_NoEffectOnIndex()
    {
        var history = new MoveOperation<int>(1, 2);
        var txOp = new RemoveOperation<int>(0);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(0));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -3, -2 }));
        });
    }

    [Test]
    public void OperationAfterMove_FromClientIndexMoveToDestination()
    {
        var history = new MoveOperation<int>(0, 1);
        var txOp = new RemoveOperation<int>(0);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(1));
            
            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -2 }));
        });
    }

    [Test]
    public void OperationAfterMove_ToClientIndexShiftUpIndex()
    {
        var history = new MoveOperation<int>(1, 0);
        var txOp = new RemoveOperation<int>(0);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(1));
            
            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -2 }));
        });
    }

    [Test]
    public void OperationAfterMove_RightwardShift_ShiftsIndexDown()
    {
        // history moves an element from 1 → 3, client add at 2 → falls into (1,3] so 2→1
        var history = new MoveOperation<int>(1, 3);
        var txOp = new RemoveOperation<int>(2);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(1));
            
            var list = new VersionedList<int>().FillNegative(5);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -1, -4, -2, -5 }));
        });
    }

    [Test]
    public void OperationAfterMove_LeftwardShift_ShiftsIndexUp()
    {
        // history moves an element from 3 → 1, client add at 2 → falls into [1,3) so 2→3
        var history = new MoveOperation<int>(4, 1);
        var txOp = new RemoveOperation<int>(2);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(3));
            
            var list = new VersionedList<int>().FillNegative(5);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -1, -5, -2, -4 }));
        });
    }

    [Test]
    public void OperationAfterMove_MoveRight_InsertIndexBetweenBounds_ShiftsIndexDown()
    {
        // history moves 1 → 4, client add at 3 falls into (1,4] so 3→2
        var history = new MoveOperation<int>(1, 4);
        var txOp = new RemoveOperation<int>(3);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(2));
            
            var list = new VersionedList<int>().FillNegative(5);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -1, -3, -5, -2}));
        });
    }

    [Test]
    public void OperationAfterMove_MoveLeft_InsertIndexBetweenBounds_ShiftsIndexUp()
    {
        // history moves 4 → 1, client add at 3 falls into [1,4) so 3→4
        var history = new MoveOperation<int>(4, 1);
        var txOp = new RemoveOperation<int>(3);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(4));
            
            var list = new VersionedList<int>().FillNegative(5);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -1, -5, -2, -3}));
        });
    }

    [Test]
    public void OperationAfterModify_NoEffectOnIndex()
    {
        var history = new ModifyOperation<int>(1, 2);
        var txOp = new RemoveOperation<int>(0);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(0));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { 2, -3}));
        });
    }
}
