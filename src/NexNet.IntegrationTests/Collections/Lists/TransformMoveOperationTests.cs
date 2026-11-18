using NexNet.Internals.Collections.Lists;
using NexNet.Internals.Collections.Versioned;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests.Collections.Lists;
public class TransformMoveOperationTests
{
    [Test]
    public async Task SingleOperation_FromStart_ToMiddle()
    {
        var txOp = new MoveOperation<int>(0, 1);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { -2, -1, -3, });

        }
    }

    [Test]
    public async Task SingleOperation_FromStart_ToEnd()
    {
        var txOp = new MoveOperation<int>(0, 2);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { -2, -3, -1 });

        }
    }

    [Test]
    public async Task SingleOperation_FromMiddle_ToStart()
    {
        var txOp = new MoveOperation<int>(1, 0);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { -2, -1, -3 });

        }
    }

    [Test]
    public async Task SingleOperation_FromMiddle_ToEnd()
    {
        var txOp = new MoveOperation<int>(1, 2);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { -1, -3, -2 });

        }
    }

    [Test]
    public async Task SingleOperation_FromEnd_ToStart()
    {
        var txOp = new MoveOperation<int>(2, 0);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { -3, -1, -2 });

        }
    }
    
    [Test]
    public async Task SingleOperation_FromEnd_ToMiddle()
    {
        var txOp = new MoveOperation<int>(2, 1);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { -1, -3, -2 });

        }
    }
    
    [Test]
    public async Task Instancing_Throws_OnFromIndexUnderflow()
    {
        // ReSharper disable once ObjectCreationAsStatement
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MoveOperation<int>(-1, 1));
    }
    
    [Test]
    public async Task Instancing_Throws_OnToIndexUnderflow()
    {
        // ReSharper disable once ObjectCreationAsStatement
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MoveOperation<int>(0, -1));
    }
    
    [Test]
    public async Task OnProcessOperation_MoveFromCurrentToCurrent_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new MoveOperation<int>(0, 1)
        {
            FromIndex = 1, // Force the index to be in an impossible state.
            ToIndex = 1, // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(2);
        await Assert.That(list.ProcessOperation(txOp, 0, out _)).IsNull();
    }
    
    [Test]
    public async Task OnProcessOperation_OnFromIndexUnderflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new MoveOperation<int>(0, 1)
        {
            FromIndex = -1 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(2);
        await Assert.That(list.ProcessOperation(txOp, 0, out _)).IsNull();
    }
    
    [Test]
    public async Task OnProcessOperation_OnToIndexUnderflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new MoveOperation<int>(0, 1)
        {
            ToIndex = -1 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(2);
        await Assert.That(list.ProcessOperation(txOp, 0, out _)).IsNull();
    }
    
    [Test]
    public async Task OnProcessOperation_OnFromIndexOverflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new MoveOperation<int>(0, 1)
        {
            FromIndex = 2 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(2);
        await Assert.That(list.ProcessOperation(txOp, 0, out _)).IsNull();
    }
    
    [Test]
    public async Task OnProcessOperation_OnToIndexOverflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new MoveOperation<int>(0, 1)
        {
            ToIndex = 2 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(2);
        await Assert.That(list.ProcessOperation(txOp, 0, out _)).IsNull();
    }
    
    [Test]
    public async Task OperationAfterInsert_AtSameIndex_From_ShiftsIndexUp()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(1, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(2);
            await Assert.That(txOp.ToIndex).IsEqualTo(3);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] {-1, 1, -3, -2});

        }
    }

    [Test]
    public async Task OperationAfterInsert_AtSameIndex_From_Across_ShiftsIndexUp()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(0, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(0);
            await Assert.That(txOp.ToIndex).IsEqualTo(3);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] {1, -2, -3, -1});

        }
    }

    [Test]
    public async Task OperationAfterInsert_AtSameIndex_To_ShiftsIndexUp()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(2, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(3);
            await Assert.That(txOp.ToIndex).IsEqualTo(2);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] {-1, 1, -3, -2});

        }
    }

    [Test]
    public async Task OperationAfterInsert_AtSameIndex_To_Across_ShiftsIndexUp()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(2, 0);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(3);
            await Assert.That(txOp.ToIndex).IsEqualTo(0);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] {-3, -1, 1, -2});

        }
    }

    [Test]
    public async Task OperationAfterInsert_Before_From_ShiftsIndexUp()
    {
        var history = new InsertOperation<int>(0, 1);
        var txOp = new MoveOperation<int>(1, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(2);
            await Assert.That(txOp.ToIndex).IsEqualTo(3);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] {1, -1, -3, -2});

        }
    }

    [Test]
    public async Task OperationAfterInsert_Before_From_Across_ShiftsIndexUp()
    {
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(0, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(0);
            await Assert.That(txOp.ToIndex).IsEqualTo(3);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] {1, -2, -3, -1});

        }
    }

    [Test]
    public async Task OperationAfterInsert_Before_To_ShiftsIndexUp()
    {
        var history = new InsertOperation<int>(0, 1);
        var txOp = new MoveOperation<int>(2, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(3);
            await Assert.That(txOp.ToIndex).IsEqualTo(2);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] {1, -1, -3, -2});

        }
    }


    [Test]
    public async Task OperationAfterInsert_Before_To_Across_ShiftsIndexUp()
    {
        var history = new InsertOperation<int>(1, 1);
        var txOp = new MoveOperation<int>(2, 0);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(3);
            await Assert.That(txOp.ToIndex).IsEqualTo(0);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] {-3, -1, 1, -2});

        }
    }

    [Test]
    public async Task OperationAfterInsert_After_From_IndexUnchanged()
    {
        var history = new InsertOperation<int>(2, 1);
        var txOp = new MoveOperation<int>(0, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(0);
            await Assert.That(txOp.ToIndex).IsEqualTo(1);

            var list = new VersionedList<int>().FillNegative(4);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] {-2, -1, 1, -3, -4});

        }
    }

    [Test]
    public async Task OperationAfterInsert_After_To_IndexUnchanged()
    {
        var history = new InsertOperation<int>(3, 1);
        var txOp = new MoveOperation<int>(2, 0);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(2);
            await Assert.That(txOp.ToIndex).IsEqualTo(0);

            var list = new VersionedList<int>().FillNegative(4);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] {-3, -1, -2, 1, -4});

        }
    }

    [Test]
    public async Task OperationAfterRemove_Before_ShiftsIndexDown()
    {
        var history = new RemoveOperation<int>(0);
        var txOp = new MoveOperation<int>(1, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(0);
            await Assert.That(txOp.ToIndex).IsEqualTo(1);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -3, -2 });

        }
    }

    [Test]
    public async Task OperationAfterRemove_After_Noop()
    {
        var history = new RemoveOperation<int>(1);
        var txOp = new MoveOperation<int>(1, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsFalse();

            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsTypeOf<NoopOperation<int>>();
            await Assert.That(list).IsEqualTo(new[] { -1 });

        }

    }

    [Test]
    public async Task OperationBeforeMoveToAfter_ShiftsToIndexDown()
    {
        var history = new MoveOperation<int>(1, 2);
        var txOp = new MoveOperation<int>(0, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(0);
            await Assert.That(txOp.ToIndex).IsEqualTo(1);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -3, -1, -2 });

        }
    }

    [Test]
    public async Task OperationAfterMove_FromClientIndexMoveToDestination()
    {
        var history = new MoveOperation<int>(0, 2);
        var txOp = new MoveOperation<int>(0, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(2);
            await Assert.That(txOp.ToIndex).IsEqualTo(0);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -1, -2, -3 });

        }
    }

    [Test]
    public async Task OperationAfterMove_ToClientIndexShiftUpIndex()
    {
        var history = new MoveOperation<int>(1, 0);
        var txOp = new MoveOperation<int>(2, 0);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(2);
            await Assert.That(txOp.ToIndex).IsEqualTo(1);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -2, -3, -1});

        }
    }

    [Test]
    public async Task OperationAfterMove_RightwardShift_ShiftsIndexDown()
    {
        // history moves an element from 1 → 3, client add at 2 → falls into (1,3] so 2→1
        var history = new MoveOperation<int>(2, 5);
        var txOp = new MoveOperation<int>(3, 6);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(2);
            await Assert.That(txOp.ToIndex).IsEqualTo(6);

            var list = new VersionedList<int>().FillNegative(8);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { -1, -2, -4, -5, -6, -3, -7, -8 });
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -1, -2, -5, -6, -3, -7, -4, -8 });

        }
    }

    [Test]
    public async Task OperationAfterMove_LeftwardShift_ShiftsIndexUp()
    {
        // history moves an element from 3 → 1, client add at 2 → falls into [1,3) so 2→3
        var history = new MoveOperation<int>(5, 2);
        var txOp = new MoveOperation<int>(3, 6);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(4);
            await Assert.That(txOp.ToIndex).IsEqualTo(6);

            var list = new VersionedList<int>().FillNegative(8);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { -1, -2, -6, -3, -4, -5, -7, -8 });
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -1, -2, -6, -3, -5, -7, -4, -8 });

        }
    }

    [Test]
    public async Task OperationAfterMove_MoveRight_InsertIndexBetweenBounds_ShiftsIndexDown()
    {
        // history moves 1 → 4, client add at 3 falls into (1,4] so 3→2
        var history = new MoveOperation<int>(1, 4);
        var txOp = new MoveOperation<int>(3, 5);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(2);
            await Assert.That(txOp.ToIndex).IsEqualTo(5);

            var list = new VersionedList<int>().FillNegative(7);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -1, -3, -5, -2, -6, -4, -7 });

        }
    }

    [Test]
    public async Task OperationAfterMove_MoveLeft_InsertIndexBetweenBounds_ShiftsIndexUp()
    {
        // history moves 4 → 1, client add at 3 falls into [1,4) so 3→4
        var history = new MoveOperation<int>(4, 1);
        var txOp = new MoveOperation<int>(3, 5);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(4);
            await Assert.That(txOp.ToIndex).IsEqualTo(5);

            var list = new VersionedList<int>().FillNegative(7);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -1, -5, -2, -3, -6, -4, -7 });

        }
    }

    [Test]
    public async Task OperationAfterModify_NoEffectOnIndex()
    {
        var history = new ModifyOperation<int>(1, 2);
        var txOp = new MoveOperation<int>(0, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.FromIndex).IsEqualTo(0);
            await Assert.That(txOp.ToIndex).IsEqualTo(2);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { 2, -3, -1 });

        }
    }
}