using NexNet.Internals.Collections.Lists;
using NexNet.Internals.Collections.Versioned;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections.Lists;

[TestFixture]
public class VersionedListFuzzTests
{
    [Test]
    public void FuzzTest_ProcessOperation_WithRandomValidOperations_DoesNotThrow()
    {
        var random = new Random(12345);
        var versionedList = new VersionedList<int>();
        var valueCounter = 0;
        var skipped = 0;
        var badOpCounter = 0;

        // Seed the list with a handful of initial items
        int initialSize = random.Next(5, 20);
        for (int i = 0; i < initialSize; i++)
        {
            var seedOp = new InsertOperation<int>(i, random.Next());
            var returnedOp = versionedList.ProcessOperation(seedOp, versionedList.Version, out var seedResult);
            Assert.That(seedResult, Is.EqualTo(ListProcessResult.Successful), "Seeding failed");
            Assert.That(returnedOp, Is.Not.Null, "Returned operation failed");
        }

        const int iterations = 100_000;
        for (int i = 0; i < iterations; i++)
        {
            // Pick a random operation type
            Operation<int> op;
            int count = versionedList.Count;

            switch (random.Next(4))
            {
                case 0: // Insert
                    // valid insert index: [0 .. count]
                    int insertIndex = random.Next(0, count);
                    op = new InsertOperation<int>(insertIndex, valueCounter++);
                    break;

                case 1: // Remove
                    if (count == 0)
                    {
                        // Skip this round and decrement the index. 
                        i--;
                        skipped++;
                        continue;
                    }
                    int removeIndex = random.Next(0, count);
                    op = new RemoveOperation<int>(removeIndex);
                    break;

                case 2: // Modify
                    if (count == 0)
                    {
                        // Skip this round and decrement the index. 
                        skipped++;
                        continue;
                    }
                    int modifyIndex = random.Next(0, count);
                    op = new ModifyOperation<int>(modifyIndex, valueCounter++);
                    break;

                default: // Move
                    if (count < 2)
                    {
                        // Skip this round and decrement the index. 
                        i--;
                        skipped++;
                        continue;
                    }
                    int from = random.Next(0, count);
                    int to;
                    do { to = random.Next(0, count); } while (to == from);
                    op = new MoveOperation<int>(from, to);
                    break;
            }

            // Choose a random valid baseVersion: [0 .. currentVersion]
            int baseVersion = random.Next((int)versionedList.MinValidVersion, versionedList.Version + 1);

            Assert.DoesNotThrow(() =>
                {
                    var resultOp = versionedList.ProcessOperation(op, baseVersion, out var result);
                    
                    // result should always be a defined enum value
                    Assert.That(Enum.IsDefined(typeof(ListProcessResult), result), Is.True,
                        $"Iteration {i}: unexpected ListProcessResult '{result}'");
                    
                    // if null was returned, only BadOperation / InvalidVersion / OutOfOperationalRange are valid
                    if (resultOp is null)
                    {
                        badOpCounter++;
                        Assert.That(result == ListProcessResult.BadOperation
                                    || result == ListProcessResult.InvalidVersion
                                    || result == ListProcessResult.OutOfOperationalRange,
                            Is.True,
                            $"Iteration {i}: returned null but result was {result}");
                    }
                }, $"Iteration {i}: throwing for operation {op.GetType().Name}");
        }
    }
}
