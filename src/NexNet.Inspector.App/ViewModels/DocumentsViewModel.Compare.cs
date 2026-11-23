using NexNet.Inspector.App.Model;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>Which documents take part in Compare (the tabs' checkboxes): exactly two at most (OQ11).</summary>
public sealed partial class DocumentsViewModel
{
    /// <summary>How many documents Compare takes.</summary>
    public const int MaxCompared = 2;

    private int _fileMarkers;

    /// <summary>How many documents are ticked for Compare.</summary>
    public int ComparedCount { get; private set; }

    /// <summary>True when exactly two documents are ticked, so Compare has its pair.</summary>
    public bool CanCompare => ComparedCount == MaxCompared;

    /// <summary>The ticked documents, in tab order.</summary>
    public IEnumerable<DocumentViewModel> Compared => Documents.Where(d => d.IsIncludedInCompare);

    /// <summary>Ticks or unticks a document; ticking a third is refused.</summary>
    /// <returns>False when refused (two others are ticked, or the document is not open).</returns>
    internal bool TrySetCompared(DocumentViewModel document, bool included)
    {
        if (!Documents.Contains(document)) return false;
        if (document.IsIncludedInCompare == included) return true;
        if (included && ComparedCount >= MaxCompared) return false;

        document.SetIncludedInCompare(included);
        ComparedCount += included ? 1 : -1;
        RefreshCompareGates();
        return true;
    }

    /// <summary>The next file's marker: files alternate blue and orange in the order they open.</summary>
    private TabMarker NextMarker(DocumentViewModel document)
    {
        if (document.Kind == DocumentKind.Live) return TabMarker.Live;
        return _fileMarkers++ % 2 == 0 ? TabMarker.FileInbound : TabMarker.FileOutbound;
    }

    private void OnDocumentAdded(DocumentViewModel document)
    {
        document.AttachTo(this, NextMarker(document));
        RefreshCompareGates();
    }

    private void OnDocumentRemoved(DocumentViewModel document)
    {
        if (document.IsIncludedInCompare)
        {
            document.SetIncludedInCompare(false);
            ComparedCount--;
        }

        document.DetachFromOwner();
        RefreshCompareGates();
    }

    private void RefreshCompareGates()
    {
        bool full = ComparedCount >= MaxCompared;
        foreach (DocumentViewModel d in Documents) d.CanIncludeInCompare = d.IsIncludedInCompare || !full;
        OnPropertyChanged(nameof(ComparedCount));
        OnPropertyChanged(nameof(CanCompare));
    }
}
