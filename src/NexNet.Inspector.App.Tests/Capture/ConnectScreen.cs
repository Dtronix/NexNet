using Avalonia.Controls;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The connect screen (PLAN §8): the empty window with the package's recents, the Connect dialog open over it, the
/// endpoint focused and its probe reached. The clock is fixed so the relative dates read as in the PNG.
/// </summary>
public static class ConnectScreen
{
    /// <summary>2026-11-20 14:02 UTC, the day the package's recents are relative to.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 20, 14, 2, 0, TimeSpan.Zero);

    public const string Reached = "Reached 10.0.4.12:7711 · server policy: recording allowed · secrets redacted · schema Chat.nnschema provided";

    public static AppSettings Settings() => new()
    {
        RecentEndpoints =
        [
            new RecentEndpoint("tcp://10.0.4.12:7711", InspectorTargetKind.Session, Now),
            new RecentEndpoint("uds:///tmp/chat-inspect.sock", InspectorTargetKind.Client, Now.AddDays(-1)),
            new RecentEndpoint("quic://staging-chat:7711", InspectorTargetKind.Session, Now.AddDays(-2)),
        ],
        RecentFiles =
        [
            new RecentFile("/captures/server-all.nntl", 3, Now),
            new RecentFile("/captures/chat-repro.nntl", 1, Now.AddHours(-3)),
            new RecentFile("/captures/reconnect-storm.nntl", 41, Now.AddDays(-3)),
        ],
        Connect = new ConnectDefaults { Endpoint = "10.0.4.12:7711", Target = InspectorTargetKind.Session, Overflow = InspectorOverflow.Drop, Snapshot = true },
    };

    public static Window Create(CaptureContext context)
    {
        // The store keeps what it last saved in memory; the file is only its backing and is removed at once.
        string path = Path.Combine(Path.GetTempPath(), "nexnet-inspector-capture-" + Guid.NewGuid().ToString("N") + ".json");
        SettingsStore store = new(path);
        store.Save(Settings());
        File.Delete(path);

        FixedTime time = new(Now);
        MainWindowViewModel viewModel = new(context.Themes, store, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, store, time), time);
        ConnectDialogViewModel dialog = viewModel.ShowConnect(null)!;
        dialog.Connect.Prober = static (_, _, _, _, _) => Task.FromResult(new EndpointProbeResult(ProbeState.Reached, null, null, Reached));
        dialog.Connect.Token = "secret-token";
        dialog.Connect.ProbeNow();
        return new MainWindow { DataContext = viewModel };
    }
}

/// <summary>A clock stopped at one instant, in UTC, so dates render the same on every machine.</summary>
public sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
