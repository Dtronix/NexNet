using NexNet.Internals.Collections.Lists;
using NexNet.Internals.Collections.Versioned;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections.Lists;

[TestFixture]
public class TransformRemoveOperationTests
{
    [Test]
    public void SingleOperation_Single()
    {
        var txOp = new RemoveOperation<int>(0);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(1);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(Array.Empty<int>()));
        });
    }

    [Test]
    public void SingleOperation_AtStart()
    {
        var txOp = new RemoveOperation<int>(0);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -2, -3 }));
        });
    }

    [Test]
    public void SingleOperation_AtMiddle()
    {
        var txOp = new RemoveOperation<int>(1);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -1, -3 }));
        });
    }

    [Test]
    public void SingleOperation_AtEnd()
    {
        var txOp = new RemoveOperation<int>(2);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -1, -2 }));
        });
    }
        
    [Test]
    public void Instancing_Throws_OnIndexUnderflow()
    {
        // ReSharper disable once ObjectCreationAsStatement
        Assert.Throws<ArgumentOutOfRangeException>(() => new RemoveOperation<int>(-1));
    }
    
    [Test]
    public void OnProcessOperation_OnIndexUnderflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new RemoveOperation<int>(0)
        {
            Index = -1 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(1);
        Assert.That(list.ProcessOperation(txOp, 0, out _), Is.Null);
    }
    
    [Test]
    public void OnProcessOperation_OnIndexOverflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new RemoveOperation<int>(0)
        {
            Index = 1 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(1);
        Assert.That(list.ProcessOperation(txOp, 0, out _), Is.Null);
    }
    
    [Test]
    public void OperationAfterInsert_AtSameIndex_ShiftsIndexUp()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new InsertOperation<int>(1, 1);
        var txOp = new RemoveOperation<int>(1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(2));

            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -1 , 1}));
        });
    }

    [Test]
    public void OperationAfterInsert_Before_ShiftsIndexUp()
    {
        var history = new InsertOperation<int>(0, 1);
        var txOp = new RemoveOperation<int>(1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(2));
            
            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { 1, -1 }));
        });
    }

    [Test]
    public void OperationAfterInsert_After_IndexUnchanged()
    {
        var history = new InsertOperation<int>(1, 1);
        var txOp = new RemoveOperation<int>(0);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.Index, Is.EqualTo(0));
            
            var list = new VersionedList<int>().FillNegative(1);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { 1 }));
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
