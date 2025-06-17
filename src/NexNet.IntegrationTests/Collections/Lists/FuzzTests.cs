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

        const int iterations = 50_000;
        for (int i = 0; i < iterations; i++)
        {
            // Pick a random operation type
            Operation<int> op;
            int count = versionedList.Count;
            var rand = random.NextSingle();
            
            if (rand < 0.001f) // 1% case
            {
                op = new ClearOperation<int>();
            }
            else if (rand < 0.001f + 0.99f / 4) // 99% split across four cases
            {
                // valid insert index: [0 .. count]
                int insertIndex = random.Next(0, count);
                op = new InsertOperation<int>(insertIndex, valueCounter++);
            }
            else if (rand < 0.01f + 0.99f / 4 * 2) // Case 3 (24.75%)
            {
                if (count == 0)
                {
                    // Skip this round and decrement the index. 
                    i--;
                    skipped++;
                    continue;
                }
                int removeIndex = random.Next(0, count);
                op = new RemoveOperation<int>(removeIndex);
            }
            else if (rand < 0.01f + 0.99f / 4 * 3) // Case 4 (24.75%)
            {
                if (count == 0)
                {
                    // Skip this round and decrement the index. 
                    skipped++;
                    continue;
                }
                int modifyIndex = random.Next(0, count);
                op = new ModifyOperation<int>(modifyIndex, valueCounter++);
            }
            else // Case 5 (24.75%)
            {
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
    
    private static void ExecuteWeightedRandomAction(Action[] actions, int[] weights)
    {
        if (actions.Length != weights.Length)
            throw new ArgumentException("Actions and weights arrays must have the same length.");

        float[] normalized = new float[weights.Length];
        float total = 0;

        for (int i = 0; i < weights.Length; i++)
        {
            total += weights[i];
        }

        for (int i = 0; i < weights.Length; i++)
        {
            normalized[i] = (float)weights[i] / total;
        }

        ExecuteRandomAction(actions, normalized);
    }
    
    private static void ExecuteRandomAction(Action[] actions, float[] probabilities)
    {
        if (actions.Length != probabilities.Length)
            throw new ArgumentException("Actions and probabilities arrays must have the same length.");

        // Validate that the sum of probabilities is approximately 1.0
        double total = 0;
        foreach (float p in probabilities)
        {
            total += p;
        }
        if (Math.Abs(total - 1.0f) > 1e-6)
        {
            throw new ArgumentException("Probabilities must sum to 1.0.");
        }

        float rand = Random.Shared.NextSingle();
        double cumulativeProbability = 0;

        for (int i = 0; i < actions.Length; i++)
        {
            cumulativeProbability += probabilities[i];
            if (rand < cumulativeProbability)
            {
                actions[i].Invoke();
                break;
            }
        }
    }
}
