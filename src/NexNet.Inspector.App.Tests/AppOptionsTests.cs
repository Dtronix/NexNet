namespace NexNet.Inspector.App.Tests;

public sealed class AppOptionsTests
{
    [Test]
    public async Task No_arguments_is_a_normal_launch()
    {
        await Assert.That(AppOptions.TryParse([], out AppOptions o, out _)).IsTrue();
        await Assert.That(o.Headless).IsFalse();
        await Assert.That(o.SelfCheck).IsFalse();
    }

    [Test]
    public async Task Headless_self_check_in_either_order()
    {
        await Assert.That(AppOptions.TryParse(["--headless", "--self-check"], out AppOptions a, out _)).IsTrue();
        await Assert.That(AppOptions.TryParse(["--self-check", "--headless"], out AppOptions b, out _)).IsTrue();
        await Assert.That(a.Headless && a.SelfCheck).IsTrue();
        await Assert.That(b).IsEqualTo(a);
    }

    [Test]
    [Arguments("--self-check", null, "--self-check needs --headless")]
    [Arguments("--headless", "--headless", "'--headless' is given twice")]
    [Arguments("--selfcheck", null, "unknown argument '--selfcheck'")]
    public async Task Bad_arguments_are_refused(string first, string? second, string expected)
    {
        string[] args = second is null ? [first] : [first, second];
        await Assert.That(AppOptions.TryParse(args, out _, out string? error)).IsFalse();
        await Assert.That(error).IsEqualTo(expected);
    }
}
