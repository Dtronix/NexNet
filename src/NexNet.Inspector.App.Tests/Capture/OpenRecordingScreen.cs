using Avalonia.Controls;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Sample;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The open-recording screen (PLAN §8): the empty window with the connect screen's recents behind the open-recording
/// dialog, opened on legacy-schema.nntl (two sessions recorded with the first Chat schema) while the workspace schema is
/// today's Chat schema, so the schema-mismatch card and its three choices show. The clock and zone are fixed, so the
/// info strip reads the same on every machine.
/// </summary>
public static class OpenRecordingScreen
{
    /// <summary>The fixture: a multi-session recording whose embedded schema differs from the workspace's.</summary>
    public const string Fixture = "legacy-schema.nntl";

    /// <summary>Where the workspace schema is said to come from.</summary>
    public const string WorkspaceSchemaPath = @"C:\src\chat\obj\Chat.nnschema";

    public static Window Create(CaptureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        MainWindowViewModel viewModel = Model(context.Themes);
        Open(viewModel);
        return new MainWindow { DataContext = viewModel };
    }

    /// <summary>The window's model: the recents, the fixed clock and the Chat workspace schema.</summary>
    public static MainWindowViewModel Model(ThemeService themes)
    {
        SettingsStore store = ConnectScreen.Store();
        FixedTime time = new(ConnectScreen.Now);
        DocumentsViewModel documents = new(AvaloniaUiDispatcher.Instance, store, time)
        {
            WorkspaceSchema = NexusSchema.Of<ChatServerNexus>(),
            WorkspaceSchemaName = WorkspaceSchemaPath,
        };
        return new MainWindowViewModel(themes, store, documents, time);
    }

    /// <summary>Opens the fixture and pumps the UI thread until its dialog is up; the open waits on the dialog's answer.</summary>
    public static (OpenRecordingDialogViewModel Dialog, Task Opening) Open(MainWindowViewModel viewModel, string fixture = Fixture)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Task opening = viewModel.OpenRecordingAsync(Wait.Fixture(fixture));
        InspectLiveScreen.Pump(() => viewModel.Dialog is OpenRecordingDialogViewModel || opening.IsCompleted);
        return (viewModel.Dialog as OpenRecordingDialogViewModel ?? throw new InvalidOperationException("the open-recording dialog did not open"), opening);
    }
}
