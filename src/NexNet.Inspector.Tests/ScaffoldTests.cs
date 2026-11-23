namespace NexNet.Inspector.Tests;

/// <summary>Keeps the suite non-empty until the first work orders add real tests; delete it then.</summary>
public class ScaffoldTests
{
    [Test]
    public async Task Suite_runs()
    {
        await Assert.That(typeof(ScaffoldTests).Assembly.GetName().Name).IsEqualTo("NexNet.Inspector.Tests");
    }
}
