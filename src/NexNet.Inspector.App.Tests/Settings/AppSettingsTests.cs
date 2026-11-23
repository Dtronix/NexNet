using NexNet.Inspector.App.Settings;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Settings;

/// <summary>The bounded, most-recent-first lists.</summary>
public sealed class AppSettingsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 23, 10, 30, 0, TimeSpan.Zero);

    [Test]
    public async Task Recent_files_are_newest_first_and_bounded()
    {
        AppSettings s = new();
        for (int i = 0; i < 15; i++) s = s.WithRecentFile($"f{i}.nntl", i, At.AddMinutes(i));

        await Assert.That(s.RecentFiles.Count).IsEqualTo(AppSettings.MaxRecent);
        await Assert.That(s.RecentFiles[0].Path).IsEqualTo("f14.nntl");
        await Assert.That(s.RecentFiles[^1].Path).IsEqualTo("f5.nntl");
    }

    [Test]
    public async Task Reopening_a_file_moves_it_to_the_front_without_a_duplicate()
    {
        AppSettings s = new AppSettings().WithRecentFile("a.nntl", 1, At).WithRecentFile("b.nntl", 2, At).WithRecentFile("a.nntl", 3, At.AddDays(1));

        await Assert.That(s.RecentFiles.Count).IsEqualTo(2);
        await Assert.That(s.RecentFiles[0]).IsEqualTo(new RecentFile("a.nntl", 3, At.AddDays(1)));
        await Assert.That(s.RecentFiles[1].Path).IsEqualTo("b.nntl");
    }

    [Test]
    public async Task An_endpoint_is_one_entry_per_target_kind()
    {
        AppSettings s = new AppSettings()
            .WithRecentEndpoint("tcp://h:1", InspectorTargetKind.Session, At)
            .WithRecentEndpoint("tcp://h:1", InspectorTargetKind.Client, At)
            .WithRecentEndpoint("TCP://H:1", InspectorTargetKind.Session, At.AddHours(1));

        await Assert.That(s.RecentEndpoints.Count).IsEqualTo(2);
        await Assert.That(s.RecentEndpoints[0]).IsEqualTo(new RecentEndpoint("TCP://H:1", InspectorTargetKind.Session, At.AddHours(1)));
        await Assert.That(s.RecentEndpoints[1].Target).IsEqualTo(InspectorTargetKind.Client);
    }

    [Test]
    public async Task A_blank_entry_is_refused()
    {
        await Assert.That(() => new AppSettings().WithRecentFile(" ", 0, At)).Throws<ArgumentException>();
        await Assert.That(() => new AppSettings().WithRecentEndpoint("", InspectorTargetKind.Session, At)).Throws<ArgumentException>();
    }
}
