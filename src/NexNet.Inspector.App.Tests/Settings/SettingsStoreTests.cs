using System.Text;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Settings;

/// <summary>settings.json: round trip through source-generated JSON, atomic writes, corrupt and hand-edited files.</summary>
public sealed class SettingsStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 23, 10, 30, 0, TimeSpan.Zero);

    private static AppSettings Full() => new AppSettings
    {
        Theme = AppTheme.Dark,
        Window = new WindowPlacement(120, 80, 1500, 960, SavedWindowState.Maximized),
        Connect = new ConnectDefaults { Endpoint = "tls://10.0.4.12:7711", Target = InspectorTargetKind.Client, Overflow = InspectorOverflow.Block, Snapshot = false },
    }
        .WithRecentFile("C:/rec/chat-repro.nntl", 1, At)
        .WithRecentFile("C:/rec/reconnect-storm.nntl", 41, At.AddDays(1))
        .WithRecentEndpoint("tcp://10.0.4.12:7711", InspectorTargetKind.Session, At)
        .WithRecentEndpoint("uds:///tmp/chat-inspect.sock", InspectorTargetKind.Client, At.AddHours(1));

    [Test]
    public async Task Settings_round_trip_through_the_file()
    {
        using TempDirectory dir = new();
        AppSettings saved = Full();
        SettingsStore store = new(dir.File("settings.json"));
        await Assert.That(store.Save(saved)).IsTrue();

        SettingsStore reader = new(dir.File("settings.json"));
        await Assert.That(reader.Load()).IsEqualTo(SettingsLoadOutcome.Loaded);
        AppSettings loaded = reader.Current;

        await Assert.That(loaded.Theme).IsEqualTo(AppTheme.Dark);
        await Assert.That(loaded.Window).IsEqualTo(saved.Window);
        await Assert.That(loaded.Connect).IsEqualTo(saved.Connect);
        await Assert.That(loaded.RecentFiles.SequenceEqual(saved.RecentFiles)).IsTrue();
        await Assert.That(loaded.RecentEndpoints.SequenceEqual(saved.RecentEndpoints)).IsTrue();
        await Assert.That(loaded.RecentFiles[0].Path).IsEqualTo("C:/rec/reconnect-storm.nntl");
    }

    [Test]
    public async Task Enums_are_written_by_name_in_camel_case()
    {
        using TempDirectory dir = new();
        SettingsStore store = new(dir.File("settings.json"));
        store.Save(Full());

        string json = await File.ReadAllTextAsync(store.Path);
        await Assert.That(json).Contains("\"theme\": \"Dark\"");
        await Assert.That(json).Contains("\"overflow\": \"Block\"");
        await Assert.That(json).Contains("\"state\": \"Maximized\"");
        await Assert.That(json).DoesNotContain("token");
    }

    [Test]
    public async Task A_missing_file_loads_defaults()
    {
        using TempDirectory dir = new();
        SettingsStore store = new(dir.File("settings.json"));

        await Assert.That(store.Load()).IsEqualTo(SettingsLoadOutcome.Missing);
        await Assert.That(store.Current.Theme).IsEqualTo(AppTheme.System);
        await Assert.That(store.Current.RecentFiles.Count).IsEqualTo(0);
        await Assert.That(store.Current.Window).IsNull();
        await Assert.That(store.Current.Connect).IsEqualTo(ConnectDefaults.Default);
        await Assert.That(File.Exists(store.Path)).IsFalse();
    }

    [Test]
    [Arguments("{ \"theme\": \"Dark\", ")]
    [Arguments("not json at all")]
    [Arguments("[1, 2, 3]")]
    [Arguments("null")]
    [Arguments("{ \"theme\": \"Purple\" }")]
    [Arguments("{ \"recentFiles\": 12 }")]
    public async Task A_corrupt_file_falls_back_to_defaults_and_is_kept_as_bak(string content)
    {
        using TempDirectory dir = new();
        string path = dir.File("settings.json");
        await File.WriteAllTextAsync(path, content);
        SettingsStore store = new(path);

        await Assert.That(store.Load()).IsEqualTo(SettingsLoadOutcome.Corrupt);
        await Assert.That(store.Current.Theme).IsEqualTo(AppTheme.System);
        await Assert.That(await File.ReadAllTextAsync(store.BackupPath)).IsEqualTo(content);

        // The next save writes a good file; the backup stays for the user.
        await Assert.That(store.Update(s => s with { Theme = AppTheme.Light })).IsTrue();
        SettingsStore reader = new(path);
        await Assert.That(reader.Load()).IsEqualTo(SettingsLoadOutcome.Loaded);
        await Assert.That(reader.Current.Theme).IsEqualTo(AppTheme.Light);
        await Assert.That(await File.ReadAllTextAsync(store.BackupPath)).IsEqualTo(content);
    }

    [Test]
    public async Task A_save_replaces_the_file_and_leaves_no_temp_file()
    {
        using TempDirectory dir = new();
        string path = dir.File("nested/deeper/settings.json");
        SettingsStore store = new(path);

        await Assert.That(store.Save(new AppSettings { Theme = AppTheme.Light })).IsTrue();
        await Assert.That(store.Save(new AppSettings { Theme = AppTheme.Dark })).IsTrue();

        SettingsStore reader = new(path);
        reader.Load();
        await Assert.That(reader.Current.Theme).IsEqualTo(AppTheme.Dark);
        string[] files = Directory.GetFiles(Path.GetDirectoryName(store.Path)!);
        await Assert.That(files.Length).IsEqualTo(1);
    }

    [Test]
    public async Task A_save_that_cannot_write_returns_false_and_keeps_the_settings_for_this_run()
    {
        using TempDirectory dir = new();
        // The settings folder's path is taken by a file, so the folder cannot be created.
        string blocker = dir.File("blocked");
        await File.WriteAllTextAsync(blocker, "x");
        SettingsStore store = new(Path.Combine(blocker, "settings.json"));

        await Assert.That(store.Save(new AppSettings { Theme = AppTheme.Dark })).IsFalse();
        await Assert.That(store.Current.Theme).IsEqualTo(AppTheme.Dark);
    }

    [Test]
    public async Task A_hand_edited_file_with_comments_and_trailing_commas_loads()
    {
        using TempDirectory dir = new();
        string path = dir.File("settings.json");
        await File.WriteAllTextAsync(path, """
            {
              // edited by hand
              "theme": "Light",
              "connect": { "endpoint": "tcp://localhost:7712", },
            }
            """);
        SettingsStore store = new(path);

        await Assert.That(store.Load()).IsEqualTo(SettingsLoadOutcome.Loaded);
        await Assert.That(store.Current.Theme).IsEqualTo(AppTheme.Light);
        await Assert.That(store.Current.Connect.Endpoint).IsEqualTo("tcp://localhost:7712");
        // Values the file leaves out keep their defaults.
        await Assert.That(store.Current.Connect.Overflow).IsEqualTo(InspectorOverflow.Drop);
        await Assert.That(store.Current.Connect.Snapshot).IsTrue();
    }

    [Test]
    public async Task A_loaded_file_is_normalized()
    {
        using TempDirectory dir = new();
        string path = dir.File("settings.json");
        StringBuilder files = new();
        for (int i = 0; i < 25; i++) files.Append($$"""{ "path": "f{{i % 15}}.nntl", "sessions": 1, "lastUsed": "2026-11-23T10:00:00+00:00" },""");
        await File.WriteAllTextAsync(path, $$"""
            {
              "recentFiles": [ { "path": " ", "sessions": 0, "lastUsed": "2026-11-23T10:00:00+00:00" }, {{files}} ],
              "recentEndpoints": null,
              "window": { "x": 0, "y": 0, "width": 0, "height": 700, "state": "Normal" }
            }
            """);
        SettingsStore store = new(path);

        await Assert.That(store.Load()).IsEqualTo(SettingsLoadOutcome.Loaded);
        await Assert.That(store.Current.RecentFiles.Count).IsEqualTo(AppSettings.MaxRecent);
        await Assert.That(store.Current.RecentFiles.Select(f => f.Path).Distinct().Count()).IsEqualTo(AppSettings.MaxRecent);
        await Assert.That(store.Current.RecentFiles[0].Path).IsEqualTo("f0.nntl");
        await Assert.That(store.Current.RecentEndpoints.Count).IsEqualTo(0);
        await Assert.That(store.Current.Window).IsNull();
        await Assert.That(store.Current.Connect).IsEqualTo(ConnectDefaults.Default);
    }

    [Test]
    public async Task An_empty_object_loads_every_default()
    {
        using TempDirectory dir = new();
        string path = dir.File("settings.json");
        await File.WriteAllTextAsync(path, "{ \"connect\": null }");
        SettingsStore store = new(path);

        await Assert.That(store.Load()).IsEqualTo(SettingsLoadOutcome.Loaded);
        await Assert.That(store.Current.Theme).IsEqualTo(AppTheme.System);
        await Assert.That(store.Current.RecentFiles.Count).IsEqualTo(0);
        await Assert.That(store.Current.RecentEndpoints.Count).IsEqualTo(0);
        await Assert.That(store.Current.Connect).IsEqualTo(ConnectDefaults.Default);
    }
}
