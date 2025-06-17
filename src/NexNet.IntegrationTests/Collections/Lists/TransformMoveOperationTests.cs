using NexNet.Internals.Collections.Lists;
using NexNet.Internals.Collections.Versioned;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections.Lists;

[TestFixture]
public class TransformMoveOperationTests
{
    [Test]
    public void SingleOperation_FromStart_ToMiddle()
    {
        var txOp = new MoveOperation<int>(0, 1);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -2, -1, -3, }));
        });
    }

    [Test]
    public void SingleOperation_FromStart_ToEnd()
    {
        var txOp = new MoveOperation<int>(0, 2);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -2, -3, -1 }));
        });
    }

    [Test]
    public void SingleOperation_FromMiddle_ToStart()
    {
        var txOp = new MoveOperation<int>(1, 0);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -2, -1, -3 }));
        });
    }

    [Test]
    public void SingleOperation_FromMiddle_ToEnd()
    {
        var txOp = new MoveOperation<int>(1, 2);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -1, -3, -2 }));
        });
    }

    [Test]
    public void SingleOperation_FromEnd_ToStart()
    {
        var txOp = new MoveOperation<int>(2, 0);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -3, -1, -2 }));
        });
    }
    
    [Test]
    public void SingleOperation_FromEnd_ToMiddle()
    {
        var txOp = new MoveOperation<int>(2, 1);

        Assert.Multiple(() =>
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -1, -3, -2 }));
        });
    }
    
    [Test]
    public void Instancing_Throws_OnFromIndexUnderflow()
    {
        // ReSharper disable once ObjectCreationAsStatement
        Assert.Throws<ArgumentOutOfRangeException>(() => new MoveOperation<int>(-1, 1));
    }
    
    [Test]
    public void Instancing_Throws_OnToIndexUnderflow()
    {
        // ReSharper disable once ObjectCreationAsStatement
        Assert.Throws<ArgumentOutOfRangeException>(() => new MoveOperation<int>(0, -1));
    }
    
    [Test]
    public void OnProcessOperation_MoveFromCurrentToCurrent_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new MoveOperation<int>(0, 1)
        {
            FromIndex = 1, // Force the index to be in an impossible state.
            ToIndex = 1, // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(2);
        Assert.That(list.ProcessOperation(txOp, 0, out _), Is.Null);
    }
    
    [Test]
    public void OnProcessOperation_OnFromIndexUnderflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new MoveOperation<int>(0, 1)
        {
            FromIndex = -1 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(2);
        Assert.That(list.ProcessOperation(txOp, 0, out _), Is.Null);
    }
    
    [Test]
    public void OnProcessOperation_OnToIndexUnderflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new MoveOperation<int>(0, 1)
        {
            ToIndex = -1 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(2);
        Assert.That(list.ProcessOperation(txOp, 0, out _), Is.Null);
    }
    
    [Test]
    public void OnProcessOperation_OnFromIndexOverflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new MoveOperation<int>(0, 1)
        {
            FromIndex = 2 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(2);
        Assert.That(list.ProcessOperation(txOp, 0, out _), Is.Null);
    }
    
    [Test]
    public void OnProcessOperation_OnToIndexOverflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new MoveOperation<int>(0, 1)
        {
            ToIndex = 2 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(2);
        Assert.That(list.ProcessOperation(txOp, 0, out _), Is.Null);
    }
    
    [Test]
    public void OperationAfterInsert_AtSameIndex_From_ShiftsIndexUp()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(1, 2);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(2));
            Assert.That(txOp.ToIndex, Is.EqualTo(3));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] {-1, 1, -3, -2}));
        });
    }

    [Test]
    public void OperationAfterInsert_AtSameIndex_From_Across_ShiftsIndexUp()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(0, 2);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(0));
            Assert.That(txOp.ToIndex, Is.EqualTo(3));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] {1, -2, -3, -1}));
        });
    }

    [Test]
    public void OperationAfterInsert_AtSameIndex_To_ShiftsIndexUp()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(2, 1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(3));
            Assert.That(txOp.ToIndex, Is.EqualTo(2));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] {-1, 1, -3, -2}));
        });
    }

    [Test]
    public void OperationAfterInsert_AtSameIndex_To_Across_ShiftsIndexUp()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(2, 0);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(3));
            Assert.That(txOp.ToIndex, Is.EqualTo(0));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] {-3, -1, 1, -2}));
        });
    }

    [Test]
    public void OperationAfterInsert_Before_From_ShiftsIndexUp()
    {
        var history = new InsertOperation<int>(0, 1);
        var txOp = new MoveOperation<int>(1, 2);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(2));
            Assert.That(txOp.ToIndex, Is.EqualTo(3));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] {1, -1, -3, -2}));
        });
    }

    [Test]
    public void OperationAfterInsert_Before_From_Across_ShiftsIndexUp()
    {
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(0, 2);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(0));
            Assert.That(txOp.ToIndex, Is.EqualTo(3));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] {1, -2, -3, -1}));
        });
    }

    [Test]
    public void OperationAfterInsert_Before_To_ShiftsIndexUp()
    {
        var history = new InsertOperation<int>(0, 1);
        var txOp = new MoveOperation<int>(2, 1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(3));
            Assert.That(txOp.ToIndex, Is.EqualTo(2));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] {1, -1, -3, -2}));
        });
    }


    [Test]
    public void OperationAfterInsert_Before_To_Across_ShiftsIndexUp()
    {
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(2, 0);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(3));
            Assert.That(txOp.ToIndex, Is.EqualTo(0));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] {-3, -1, 1, -2}));
        });
    }

    [Test]
    public void OperationAfterInsert_After_From_IndexUnchanged()
    {
        var history = new InsertOperation<int>(2, 1);
        var txOp = new MoveOperation<int>(0, 1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(0));
            Assert.That(txOp.ToIndex, Is.EqualTo(1));
            
            var list = new VersionedList<int>().FillNegative(4);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] {-2, -1, 1, -3, -4}));
        });
    }

    [Test]
    public void OperationAfterInsert_After_To_IndexUnchanged()
    {
        var history = new InsertOperation<int>(3, 1);
        var txOp = new MoveOperation<int>(2, 0);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(2));
            Assert.That(txOp.ToIndex, Is.EqualTo(0));
            
            var list = new VersionedList<int>().FillNegative(4);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] {-3, -1, -2, 1, -4}));
        });
    }

    [Test]
    public void OperationAfterRemove_Before_ShiftsIndexDown()
    {
        var history = new RemoveOperation<int>(0);
        var txOp = new MoveOperation<int>(1, 2);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(0));
            Assert.That(txOp.ToIndex, Is.EqualTo(1));

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -3, -2 }));
        });
    }

    [Test]
    public void OperationAfterRemove_After_Noop()
    {
        var history = new RemoveOperation<int>(1);
        var txOp = new MoveOperation<int>(1, 2);
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
    public void OperationBeforeMoveToAfter_ShiftsToIndexDown()
    {
        var history = new MoveOperation<int>(1, 2);
        var txOp = new MoveOperation<int>(0, 2);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(0));
            Assert.That(txOp.ToIndex, Is.EqualTo(1));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -3, -1, -2 }));
        });
    }

    [Test]
    public void OperationAfterMove_FromClientIndexMoveToDestination()
    {
        var history = new MoveOperation<int>(0, 2);
        var txOp = new MoveOperation<int>(0, 1);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(2));
            Assert.That(txOp.ToIndex, Is.EqualTo(0));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -1, -2, -3 }));
        });
    }

    [Test]
    public void OperationAfterMove_ToClientIndexShiftUpIndex()
    {
        var history = new MoveOperation<int>(1, 0);
        var txOp = new MoveOperation<int>(2, 0);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(2));
            Assert.That(txOp.ToIndex, Is.EqualTo(1));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -2, -3, -1}));
        });
    }

    [Test]
    public void OperationAfterMove_RightwardShift_ShiftsIndexDown()
    {
        // history moves an element from 1 → 3, client add at 2 → falls into (1,3] so 2→1
        var history = new MoveOperation<int>(2, 5);
        var txOp = new MoveOperation<int>(3, 6);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(2));
            Assert.That(txOp.ToIndex, Is.EqualTo(6));

            var list = new VersionedList<int>().FillNegative(8);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -1, -2, -4, -5, -6, -3, -7, -8 }));
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -1, -2, -5, -6, -3, -7, -4, -8 }));
        });
    }

    [Test]
    public void OperationAfterMove_LeftwardShift_ShiftsIndexUp()
    {
        // history moves an element from 3 → 1, client add at 2 → falls into [1,3) so 2→3
        var history = new MoveOperation<int>(5, 2);
        var txOp = new MoveOperation<int>(3, 6);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(4));
            Assert.That(txOp.ToIndex, Is.EqualTo(6));

            var list = new VersionedList<int>().FillNegative(8);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list, Is.EqualTo(new[] { -1, -2, -6, -3, -4, -5, -7, -8 }));
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -1, -2, -6, -3, -5, -7, -4, -8 }));
        });
    }

    [Test]
    public void OperationAfterMove_MoveRight_InsertIndexBetweenBounds_ShiftsIndexDown()
    {
        // history moves 1 → 4, client add at 3 falls into (1,4] so 3→2
        var history = new MoveOperation<int>(1, 4);
        var txOp = new MoveOperation<int>(3, 5);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(2));
            Assert.That(txOp.ToIndex, Is.EqualTo(5));
            
            var list = new VersionedList<int>().FillNegative(7);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -1, -3, -5, -2, -6, -4, -7 }));
        });
    }

    [Test]
    public void OperationAfterMove_MoveLeft_InsertIndexBetweenBounds_ShiftsIndexUp()
    {
        // history moves 4 → 1, client add at 3 falls into [1,4) so 3→4
        var history = new MoveOperation<int>(4, 1);
        var txOp = new MoveOperation<int>(3, 5);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(4));
            Assert.That(txOp.ToIndex, Is.EqualTo(5));
            
            var list = new VersionedList<int>().FillNegative(7);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { -1, -5, -2, -3, -6, -4, -7 }));
        });
    }

    [Test]
    public void OperationAfterModify_NoEffectOnIndex()
    {
        var history = new ModifyOperation<int>(1, 2);
        var txOp = new MoveOperation<int>(0, 2);
        var listOp = txOp.Clone();

        Assert.Multiple(() =>
        {
            Assert.That(txOp.TransformAgainst(history), Is.True);
            Assert.That(txOp.FromIndex, Is.EqualTo(0));
            Assert.That(txOp.ToIndex, Is.EqualTo(2));
            
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            Assert.That(list.ProcessOperation(listOp, 0, out _), Is.EqualTo(txOp));
            Assert.That(list, Is.EqualTo(new[] { 2, -3, -1 }));
        });
    }
}
