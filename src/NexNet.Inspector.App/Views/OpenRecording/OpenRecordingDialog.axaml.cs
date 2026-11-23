using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.OpenRecording;

/// <summary>
/// The open-recording dialog's view. The two warning cards' texts mix the UI font with mono runs (hashes, the schema
/// path, the repair command), so they are built here from the view-model's pieces.
/// </summary>
public sealed partial class OpenRecordingDialog : UserControl
{
    private OpenRecordingDialogViewModel? _model;

    public OpenRecordingDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as OpenRecordingDialogViewModel;
        if (_model is null) return;
        _model.PropertyChanged += OnModelChanged;
        Fill(DamageText, _model.DamageDetail);
        Fill(SchemaText, _model.SchemaDetail);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenRecordingDialogViewModel.SchemaDetail) && _model is not null)
            Fill(SchemaText, _model.SchemaDetail);
    }

    private static void Fill(TextBlock target, IReadOnlyList<TextPiece> pieces)
    {
        InlineCollection inlines = target.Inlines ??= [];
        inlines.Clear();
        foreach (TextPiece piece in pieces)
        {
            Run run = new(piece.Text);
            if (piece.Mono) run.Classes.Add("mono");
            inlines.Add(run);
        }
    }
}
