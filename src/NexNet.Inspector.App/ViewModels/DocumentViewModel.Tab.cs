using NexNet.Inspector.App.Model;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>The document as a title-bar tab: its marker and its Compare inclusion.</summary>
public abstract partial class DocumentViewModel
{
    private DocumentsViewModel? _owner;
    private bool _includedInCompare;
    private bool _canIncludeInCompare = true;

    /// <summary>The tab's leading marker; set once when the document opens.</summary>
    public TabMarker Marker { get; private set; }

    /// <summary>True for a live session (the marker is a circle).</summary>
    public bool IsLive => Kind == DocumentKind.Live;

    /// <summary>True when the marker takes the second file colour (outbound orange).</summary>
    public bool HasOutboundMarker => Marker == TabMarker.FileOutbound;

    /// <summary>
    /// The Compare checkbox. At most <see cref="DocumentsViewModel.MaxCompared"/> documents take part (OQ11): ticking a
    /// third is refused, and the refusal is announced so a two-way bound checkbox flips back.
    /// </summary>
    public bool IsIncludedInCompare
    {
        get => _includedInCompare;
        set
        {
            if (value == _includedInCompare) return;
            if (_owner is null || !_owner.TrySetCompared(this, value)) OnPropertyChanged();
        }
    }

    /// <summary>False while two other documents are ticked: the checkbox is disabled so a third cannot be.</summary>
    public bool CanIncludeInCompare
    {
        get => _canIncludeInCompare;
        internal set => SetProperty(ref _canIncludeInCompare, value);
    }

    /// <summary>The close button's accessible name.</summary>
    public string CloseLabel => "Close " + Title;

    internal void AttachTo(DocumentsViewModel owner, TabMarker marker)
    {
        _owner = owner;
        Marker = marker;
        OnPropertyChanged(nameof(Marker));
        OnPropertyChanged(nameof(HasOutboundMarker));
    }

    internal void DetachFromOwner() => _owner = null;

    internal void SetIncludedInCompare(bool value)
    {
        if (SetProperty(ref _includedInCompare, value, nameof(IsIncludedInCompare)))
            CanIncludeInCompare = true;
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(CloseLabel));
}
