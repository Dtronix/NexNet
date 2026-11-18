using NexNet.Internals.Collections.Lists;
using NexNet.Internals.Collections.Versioned;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests.Collections.Lists;
public class TransformInsertOperationTests
{
    
    [Test]
    public async Task SingleOperation_InEmpty()
    {
        var txOp = new InsertOperation<int>(0, 1);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>();
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { 1 });

        }
    }
    
    [Test]
    public async Task MultipleOperations()
    {
        var txOp = new InsertOperation<int>(0, 1);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>();
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { 1 });

        }
    }

    [Test]
    public async Task SingleOperation_AtStart()
    {
        var txOp = new InsertOperation<int>(0, 1);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { 1, -1, -2 });

        }
    }

    [Test]
    public async Task SingleOperation_AtMiddle()
    {
        var txOp = new InsertOperation<int>(1, 1);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { -1, 1, -2 });

        }
    }

    [Test]
    public async Task SingleOperation_AtEnd()
    {
        var txOp = new InsertOperation<int>(2, 1);

        using (Assert.Multiple())
        {
            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(txOp, 0, out _);
            await Assert.That(list).IsEqualTo(new[] { -1, -2, 1 });

        }
    }
    
    [Test]
    public async Task Instancing_Throws_OnIndexUnderflow()
    {
        // ReSharper disable once ObjectCreationAsStatement
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InsertOperation<int>(-1, 1));
    }
    
    [Test]
    public async Task OnProcessOperation_OnIndexUnderflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new InsertOperation<int>(0, 1)
        {
            Index = -1 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(1);
        await Assert.That(list.ProcessOperation(txOp, 0, out _)).IsNull();
    }
    
    [Test]
    public async Task OnProcessOperation_OnIndexOverflow_Noop()
    {
        // These operations could happen if the client is feeding bad data to the server.
        var txOp = new InsertOperation<int>(0, 1)
        {
            Index = 2 // Force the index to be in an impossible state.
        };
        var list = new VersionedList<int>().FillNegative(1);
        await Assert.That(list.ProcessOperation(txOp, 0, out _)).IsNull();
    }

    [Test]
    public async Task OperationAfterInsert_AtSameIndex_ShiftsIndexUp()
    {
        // history inserts at 1, client also wanted to insert at 1 → should bump to 2
        var history = new InsertOperation<int>(0, 1);
        var txOp = new InsertOperation<int>(0, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(1);

            var list = new VersionedList<int>();
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { 1, 2 });

        }
    }

    [Test]
    public async Task OperationAfterInsert_Before_ShiftsIndexUp()
    {
        var history = new InsertOperation<int>(0, 1);
        var txOp = new InsertOperation<int>(1, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(2);

            var list = new VersionedList<int>().FillNegative(1);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { 1, -1, 2 });

        }
    }

    [Test]
    public async Task OperationAfterInsert_After_IndexUnchanged()
    {
        var history = new InsertOperation<int>(1, 1);
        var txOp = new InsertOperation<int>(0, 2);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(0);

            var list = new VersionedList<int>().FillNegative(1);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { 2, -1, 1 });

        }
    }

    [Test]
    public async Task OperationAfterRemove_Before_ShiftsIndexDown()
    {
        var history = new RemoveOperation<int>(0);
        var txOp = new InsertOperation<int>(1, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(0);

            var list = new VersionedList<int>().FillNegative(1);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { 1 });

        }
    }

    [Test]
    public async Task OperationAfterRemove_After_IndexUnchanged()
    {
        var history = new RemoveOperation<int>(1);
        var txOp = new InsertOperation<int>(1, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(1);

            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -1, 1 });

        }
    }

    [Test]
    public async Task OperationAfterMove_NoEffectOnIndex()
    {
        var history = new MoveOperation<int>(1, 2);
        var txOp = new InsertOperation<int>(0, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(0);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { 1, -1, -3, -2 });

        }
    }

    [Test]
    public async Task OperationAfterMove_FromClientIndexMoveToDestination()
    {
        // Since insert is inserting on the 0 index, but the 0 index was moved to the index of 1,
        // the update position will be updated to the new destination.
        var history = new MoveOperation<int>(0, 2);
        var txOp = new InsertOperation<int>(0, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(2);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -2, -3, 1, -1 });

        }
    }

    [Test]
    public async Task OperationAfterMove_ToClientIndexShiftUpIndex()
    {
        var history = new MoveOperation<int>(2, 0);
        var txOp = new InsertOperation<int>(0, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(1);

            var list = new VersionedList<int>().FillNegative(3);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -3, 1, -1, -2 });

        }
    }

    [Test]
    public async Task OperationAfterMove_RightwardShift_ShiftsIndexDown()
    {
        // history moves an element from 1 → 3, client add at 2 → falls into (1,3] so 2→1
        var history = new MoveOperation<int>(1, 3);
        var txOp = new InsertOperation<int>(2, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(1);

            var list = new VersionedList<int>().FillNegative(4);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -1, 1, -3, -4, -2 });

        }
    }

    [Test]
    public async Task OperationAfterMove_LeftwardShift_ShiftsIndexUp()
    {
        // history moves an element from 3 → 1, client add at 2 → falls into [1,3) so 2→3
        var history = new MoveOperation<int>(3, 1);
        var txOp = new InsertOperation<int>(2, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(3);

            var list = new VersionedList<int>().FillNegative(4);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -1, -4, -2, 1, -3 });

        }
    }

    [Test]
    public async Task OperationAfterMove_MoveRight_InsertIndexBetweenBounds_ShiftsIndexDown()
    {
        // history moves 1 → 4, client add at 3 falls into (1,4] so 3→2
        var history = new MoveOperation<int>(1, 4);
        var txOp = new InsertOperation<int>(3, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(2);

            var list = new VersionedList<int>().FillNegative(5);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -1, -3, 1, -4, -5, -2 });

        }
    }

    [Test]
    public async Task OperationAfterMove_MoveLeft_InsertIndexBetweenBounds_ShiftsIndexUp()
    {
        // history moves 4 → 1, client add at 3 falls into [1,4) so 3→4
        var history = new MoveOperation<int>(4, 1);
        var txOp = new InsertOperation<int>(3, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(4);

            var list = new VersionedList<int>().FillNegative(5);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { -1, -5, -2, -3, 1, -4 });

        }
    }

    [Test]
    public async Task OperationAfterModify_NoEffectOnIndex()
    {
        var history = new ModifyOperation<int>(1, 2);
        var txOp = new InsertOperation<int>(0, 1);
        var listOp = txOp.Clone();

        using (Assert.Multiple())
        {
            await Assert.That(txOp.TransformAgainst(history)).IsTrue();
            await Assert.That(txOp.Index).IsEqualTo(0);

            var list = new VersionedList<int>().FillNegative(2);
            list.ProcessOperation(history, 0, out _);
            await Assert.That(list.ProcessOperation(listOp, 0, out _)).IsEqualTo(txOp);
            await Assert.That(list).IsEqualTo(new[] { 1, -1, 2 });

        }
    }
}