using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// A real live connection for the screens whose title bar shows one (inspect-app-menu, inspect-session-start): an
/// in-process Chat server with an inspector endpoint that allows recording, three Chat clients connected, and a live
/// document attached to the first session through the Connect flow. The recorded fixture stays the active document, so
/// the panes show its data while the title bar shows the live connection (Server · endpoint · Session ▾ · REC).
/// Everything is torn down when the window closes.
/// </summary>
internal sealed class LiveConnection
{
    private readonly ChatServerHarness _harness;
    private readonly TempDirectory _recordings = new();

    private LiveConnection(ChatServerHarness harness) => _harness = harness;

    /// <summary>Starts the server and clients and attaches <paramref name="viewModel"/>'s documents to the first session. UI thread.</summary>
    public static LiveConnection Attach(MainWindowViewModel viewModel, int clients = 3)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ChatServerHarness harness = Run(ChatServerHarness.StartAsync(allowRecording: true));
        LiveConnection live = new(harness);
        viewModel.Chrome.Session.RecordingDirectory = () => live._recordings.Path;
        for (int i = 0; i < clients; i++) Run(harness.ConnectClientAsync());
        Run(harness.WaitForSessionAsync(clients));

        ConnectViewModel connect = viewModel.Documents.CreateConnect();
        connect.Endpoint = harness.EndpointAddress;
        connect.Token = ChatServerHarness.Token;
        Run(connect.ConnectCommand.ExecuteAsync(null));
        if (connect.Sessions.Count == 0) throw new InvalidOperationException("the endpoint listed no session: " + connect.ConnectError);
        Run(connect.AttachCommand.ExecuteAsync(connect.Sessions[0]));
        if (connect.Document is null) throw new InvalidOperationException("the attach failed: " + connect.ConnectError);
        Run(connect.DisposeAsync().AsTask());
        return live;
    }

    /// <summary>Starts Record all on the live connection (the title bar's ALL · n).</summary>
    public static void RecordAll(MainWindowViewModel viewModel)
    {
        SessionAreaViewModel session = viewModel.Chrome.Session;
        Run(session.RecordAllCommand.ExecuteAsync(null));
        if (!session.IsRecordingAll) throw new InvalidOperationException("Record all did not start: " + session.Error);
        Run(session.RefreshSessionsAsync());
    }

    /// <summary>Stops everything once <paramref name="window"/> closes.</summary>
    public void TearDownWith(Avalonia.Controls.Window window, MainWindowViewModel viewModel) => window.Closed += (_, _) =>
    {
        Run(viewModel.Chrome.Session.DisposeAsync().AsTask());
        Run(viewModel.Documents.DisposeAsync().AsTask());
        Run(_harness.DisposeAsync().AsTask());
        _recordings.Dispose();
    };

    // The harness and the documents continue on the UI thread: run its jobs until the task is done.
    private static void Run(Task task)
    {
        InspectLiveScreen.Pump(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static T Run<T>(Task<T> task)
    {
        InspectLiveScreen.Pump(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }
}
