using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The main window's state: the theme choice, the open documents (with the sync bus every view follows), the empty
/// window's home, the title bar's chrome and the dialog shown over it.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public const string AppName = "NexNet Inspector";

    private readonly ThemeService _themes;
    private readonly SettingsStore? _store;
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
        Documents = documents ?? new DocumentsViewModel(AvaloniaUiDispatcher.Instance, store);
        Chrome = new ShellChromeViewModel(Documents);
        Chrome.Requested += OnShellRequested;
        Inspect = new InspectWorkspaceViewModel(Documents, Chrome);
        Home = new HomeViewModel(() => _store?.Current ?? new AppSettings(), time ?? TimeProvider.System, recent => ShowConnect(recent), OpenRecordingAsync);
        Documents.PropertyChanged += (_, e) =>
        {
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

    /// <summary>Opens a recording as a document (the drop target, the recents, the picker).</summary>
    public Task OpenRecordingAsync(string path) => Documents.OpenRecordingAsync(path);

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

    partial void OnThemeChanged(AppTheme value)
    {
        // The constructor's first assignment mirrors what is already applied and saved.
        if (!_ready) return;
        _themes.Theme = value;
        _store?.Update(s => s with { Theme = value });
    }
}
