using Avalonia.Controls;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The command-palette screen (PLAN §8): inspect-live behind the palette, queried for the traced SendAttachment's
/// invocation id (the package's "812"; the fixture numbers its invocations itself), so the results show that
/// invocation first under ENTITIES, then its frames, then the suggested commands.
/// </summary>
public static class CommandPaletteScreen
{
    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        InspectLiveScreen.Picks picks = InspectLiveScreen.Open(viewModel);
        MainWindow window = new() { DataContext = viewModel };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        viewModel.ShowPalette(Query(picks.Document.Session!));
        return window;
    }

    /// <summary>The screen's query: the fixture's SendAttachment invocation id.</summary>
    public static string Query(CaptureSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (session.Gate)
        {
            foreach (Core.Assembly.InvocationRow row in session.Assembler.Invocations)
            {
                if (session.Decoder.TryGetMethod(row, out SchemaMethod? method) && method.Name == "SendAttachment")
                    return row.InvocationId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidOperationException("the fixture has no SendAttachment");
    }
}
