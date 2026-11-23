using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>A document with no capture behind it: a tab's title, kind and unsaved flag, for the tab strip's tests and gallery.</summary>
internal sealed class TabDocument : DocumentViewModel
{
    public TabDocument(IUiDispatcher ui, DocumentKind kind, string title, bool dirty = false)
        : base(kind, ui)
    {
        Title = title;
        IsDirty = dirty;
    }

    public bool Disposed { get; private set; }

    public override ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
