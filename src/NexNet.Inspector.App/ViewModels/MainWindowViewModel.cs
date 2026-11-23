using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The main window's state: the theme choice, the open documents (with the sync bus every view follows), the empty
/// window's home, the title bar's chrome, the dialog shown over it, the app's commands and the command palette.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public const string AppName = "NexNet Inspector";

    private readonly ThemeService _themes;
    private readonly SettingsStore? _store;
    private readonly TimeProvider _time;
    private readonly bool _ready;

    /// <param name="themes">Applies the theme choice.</param>
    /// <param name="store">Persists the choice; null keeps it for this run only (self-check, tests).</param>
    /// <param name="documents">The documents; by default a new set posting to Avalonia's UI thread and saving recents to <paramref name="store"/>.</param>
    /// <param name="time">The clock of the home's relative dates; the system clock by default.</param>
    public MainWindowViewModel(ThemeService themes, SettingsStore? store, DocumentsViewModel? documents = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(themes);
        _themes = themes;
        _store = store;
        _time = time ?? TimeProvider.System;
        Documents = documents ?? new DocumentsViewModel(AvaloniaUiDispatcher.Instance, store);
        Documents.SessionPicker ??= PickSession;
        Chrome = new ShellChromeViewModel(Documents);
        Chrome.Requested += OnShellRequested;
        Inspect = new InspectWorkspaceViewModel(Documents, Chrome) { ShowDialog = ShowGoToOffset };
        Home = new HomeViewModel(() => _store?.Current ?? new AppSettings(), _time, recent => ShowConnect(recent), OpenRecordingAsync);
        Palette = new CommandPaletteViewModel(Documents, Commands);
        BindCommands();
        Documents.PropertyChanged += (_, e) =>
        {
            // Document-dependent commands (go to time, next document) change state with the documents.
            if (e.PropertyName is nameof(DocumentsViewModel.Active) or nameof(DocumentsViewModel.HasDocuments)) Commands.Refresh();

            // Back to the empty window: the recents may have grown while documents were open.
            if (e.PropertyName == nameof(DocumentsViewModel.HasDocuments) && !Documents.HasDocuments) Home.Refresh();
        };
        Theme = themes.Theme;
        _ready = true;
    }

    /// <summary>The open documents, the active one and the sync bus.</summary>
    public DocumentsViewModel Documents { get; }

    /// <summary>The title bar and the window's collapse state (OQ10).</summary>
    public ShellChromeViewModel Chrome { get; }

    /// <summary>The Inspect workspace: timeline, stream panes and inspector over the active document.</summary>
    public InspectWorkspaceViewModel Inspect { get; }

    /// <summary>The empty window: action cards, recents, the drop target.</summary>
    public HomeViewModel Home { get; }

    public string Title => AppName;

    /// <summary>The app's commands, shared by the command palette and the app menu.</summary>
    public CommandRegistry Commands { get; } = new();

    /// <summary>The command palette (Ctrl+Shift+P).</summary>
    public CommandPaletteViewModel Palette { get; }

    /// <summary>The dialog over the window (its view comes from the window's data templates); null when none is open.</summary>
    [ObservableProperty]
    public partial object? Dialog { get; private set; }

    /// <summary>The user's theme choice: applied at once and saved.</summary>
    [ObservableProperty]
    public partial AppTheme Theme { get; set; }

    [RelayCommand]
    private void SetTheme(AppTheme theme) => Theme = theme;

    /// <summary>Ctrl+K: the Connect dialog, from the saved defaults.</summary>
    [RelayCommand]
    private void Connect() => ShowConnect(null);

    /// <summary>Ctrl+O: the file picker, then the recording.</summary>
    [RelayCommand]
    private Task Open() => Home.OpenFileCommand.ExecuteAsync(null);

    /// <summary>Opens the command palette with <paramref name="query"/> (":" for Go to time), unless a dialog is open.</summary>
    /// <returns>True when the palette is open.</returns>
    public bool ShowPalette(string? query = null)
    {
        if (Dialog is not null) return false;
        Palette.Open(query);
        return true;
    }

    /// <summary>Opens the Connect dialog (unless a dialog is already open), prefilled from a recent endpoint when given.</summary>
    /// <returns>The dialog's model; null when another dialog was open.</returns>
    public ConnectDialogViewModel? ShowConnect(RecentEndpoint? recent)
    {
        if (Dialog is not null) return null;
        ConnectViewModel connect = Documents.CreateConnect();
        if (recent is { } r)
        {
            connect.Endpoint = r.Endpoint;
            connect.Target = r.Target;
        }

        ConnectDialogViewModel dialog = new(connect, CloseDialog);
        Dialog = dialog;
        return dialog;
    }

    /// <summary>Opens the hex view's "Go to offset…" dialog (unless a dialog is already open); it closes itself.</summary>
    public bool ShowGoToOffset(GoToOffsetDialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (Dialog is not null) return false;
        dialog.Closed += d =>
        {
            if (ReferenceEquals(Dialog, d)) Dialog = null;
        };
        Dialog = dialog;
        return true;
    }

    /// <summary>The title bar's requests: its Connect pill and the tab strip's + (the logo's menu is I28's).</summary>
    private void OnShellRequested(ShellRequest request)
    {
        switch (request)
        {
            case ShellRequest.Connect:
                ShowConnect(null);
                break;
            case ShellRequest.Open:
                OpenCommand.Execute(null);
                break;
        }
    }

    /// <summary>
    /// Opens a recording as a document (the drop target, the recents, the picker). One that needs a choice asks the
    /// open-recording dialog (<see cref="DocumentsViewModel.NeedsPicker"/>); one that cannot be read at all shows that
    /// dialog's error state.
    /// </summary>
    public async Task OpenRecordingAsync(string path)
    {
        try
        {
            await Documents.OpenRecordingAsync(path).ConfigureAwait(true);
        }
        catch (Exception e) when (e is NntlFormatException or IOException or UnauthorizedAccessException)
        {
            if (Dialog is null) Dialog = OpenRecordingDialogViewModel.Failed(path, e.Message, CloseDialog);
            else throw;
        }
    }

    /// <summary>The open-recording dialog as the documents' session picker; another open dialog answers cancel.</summary>
    private Task<RecordingOpenChoice?> PickSession(RecordingFile recording)
    {
        if (Dialog is not null) return Task.FromResult<RecordingOpenChoice?>(null);
        OpenRecordingDialogViewModel dialog = new(recording, Documents.WorkspaceSchema, Documents.WorkspaceSchemaName, _time.LocalTimeZone,
            CloseDialog, Documents.SessionComparer);
        Dialog = dialog;
        return dialog.Result;
    }

    private void CloseDialog(OpenRecordingDialogViewModel dialog)
    {
        if (ReferenceEquals(Dialog, dialog)) Dialog = null;
    }

    private async void CloseDialog(ConnectDialogViewModel dialog)
    {
        if (ReferenceEquals(Dialog, dialog)) Dialog = null;
        try
        {
            await dialog.DisposeAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // A connection that fails while closing has nothing left to report to: the dialog is gone.
            System.Diagnostics.Debug.WriteLine("connect dialog close: " + e.Message);
        }
    }

    // The commands whose behaviour lives here; others are bound by their owners (the app menu, recording, compare).
    private void BindCommands()
    {
        Commands.Bind(AppCommand.CommandPalette, () => ShowPalette(), () => Dialog is null);
        Commands.Bind(AppCommand.OpenRecording, () => OpenCommand.Execute(null), () => Dialog is null);
        Commands.Bind(AppCommand.Connect, () => ShowConnect(null), () => Dialog is null);
        Commands.Bind(AppCommand.GoToTime, () => ShowPalette(":"), () => Dialog is null && Documents.Active?.Session is not null);
        Commands.Bind(AppCommand.ToggleSplit, () => Inspect.ToggleSplitCommand.Execute(null), () => Inspect.IsShown && Inspect.CanSplit);
        Commands.Bind(AppCommand.NextDocument, () => Documents.Cycle(backwards: false), () => Documents.Documents.Count > 1);
        Commands.Bind(AppCommand.PreviousDocument, () => Documents.Cycle(backwards: true), () => Documents.Documents.Count > 1);
        Commands.Bind(AppCommand.ThemeDark, () => Theme = AppTheme.Dark);
        Commands.Bind(AppCommand.ThemeLight, () => Theme = AppTheme.Light);
        Commands.Bind(AppCommand.ThemeSystem, () => Theme = AppTheme.System);
    }

    partial void OnDialogChanged(object? value)
    {
        // An open dialog owns the keyboard: the palette closes and the window's commands wait for it.
        if (value is not null) Palette.Close();
        Commands.Refresh();
    }

    partial void OnThemeChanged(AppTheme value)
    {
        // The constructor's first assignment mirrors what is already applied and saved.
        if (!_ready) return;
        _themes.Theme = value;
        _store?.Update(s => s with { Theme = value });
    }
}
