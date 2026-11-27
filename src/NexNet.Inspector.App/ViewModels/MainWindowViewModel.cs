using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The main window's state: the theme choice, the open documents (with the sync bus every view follows), the empty
/// window's home, the title bar's chrome, the dialog shown over it, the app's commands and the command palette, and the
/// application (logo) menu: File (open, save, recents), Session (connect, record all, switch session, workspace schema,
/// overflow mode), View (theme, command palette) and Help, its items read from the command registry.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public const string AppName = "NexNet Inspector";

    private readonly ThemeService _themes;
    private readonly SettingsStore? _store;
    private readonly TimeProvider _time;
    private readonly bool _ready;
    private bool _hadWindow;

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
        Documents.ClosePrompt ??= PromptUnsaved;
        Documents.SaveHandler ??= SaveDocumentAsync;
        Chrome = new ShellChromeViewModel(Documents);
        Ended = new SessionEndedViewModel(Documents, () => Chrome.Session.IsPopoverOpen = true);
        Chrome.Requested += OnShellRequested;
        Inspect = new InspectWorkspaceViewModel(Documents, Chrome) { ShowDialog = ShowGoToOffset, ShowExport = kind => ShowExportSection(kind) is not null };
        Compare = new CompareWorkspaceViewModel(Documents, Chrome);
        Documents.SessionComparer ??= Compare.CompareRecordingAsync;
        Panes = new PaneSizesViewModel(store);
        Inspect.Sizes = Panes;
        Compare.Sizes = Panes;
        Home = new HomeViewModel(() => _store?.Current ?? new AppSettings(), _time, recent => ShowConnect(recent), OpenRecordingAsync);
        Palette = new CommandPaletteViewModel(Documents, Commands);
        BindCommands();
        Documents.Sync.Changed += changes =>
        {
            // The scope commands follow the active document's scope and whether it has a window to scope to.
            bool hasWindow = !Documents.Sync.Window.IsEmpty;
            if ((changes & SyncChanges.Scope) != 0 || hasWindow != _hadWindow) Commands.Refresh();
            _hadWindow = hasWindow;
        };
        Documents.PropertyChanged += (_, e) =>
        {
            // Document-dependent commands (go to time, next document) change state with the documents.
            if (e.PropertyName is nameof(DocumentsViewModel.Active) or nameof(DocumentsViewModel.HasDocuments)) Commands.Refresh();

            // Back to the empty window: the recents may have grown while documents were open.
            if (e.PropertyName == nameof(DocumentsViewModel.HasDocuments) && !Documents.HasDocuments) Home.Refresh();
        };
        Theme = themes.Theme;
        ThemeChoice = new EnumChoice<AppTheme>([(AppTheme.Dark, "Dark"), (AppTheme.Light, "Light"), (AppTheme.System, "System")], Theme);
        ThemeChoice.ValueChanged += (_, value) => Theme = value;
        Chrome.Session.Sessions.CollectionChanged += OnSessionsChanged;
        _ready = true;
    }

    /// <summary>The app menu's Theme row: Dark | Light | System (OQ15: System follows the OS live).</summary>
    public EnumChoice<AppTheme> ThemeChoice { get; }

    /// <summary>The application menu under the logo is open.</summary>
    [ObservableProperty]
    public partial bool IsAppMenuOpen { get; set; }

    /// <summary>File › Recent ▸: the recent recordings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentItems))]
    public partial IReadOnlyList<MenuChoice> RecentItems { get; private set; } = [];

    public bool HasRecentItems => RecentItems.Count > 0;

    /// <summary>Session › Switch session ▸: the live connection's sessions, the attached one checked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessionItems))]
    public partial IReadOnlyList<MenuChoice> SessionItems { get; private set; } = [];

    public bool HasSessionItems => SessionItems.Count > 0;

    /// <summary>Session › Schema ▸: load or clear the workspace schema.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MenuChoice> SchemaItems { get; private set; } = [];

    /// <summary>Session › Overflow mode ▸: Drop or Block for later attaches.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MenuChoice> OverflowItems { get; private set; } = [];

    /// <summary>"Schema · Chat.nnschema (embedded)": what decodes the active document.</summary>
    [ObservableProperty]
    public partial string SchemaHeader { get; private set; } = "Schema";

    /// <summary>"Overflow mode · Drop".</summary>
    [ObservableProperty]
    public partial string OverflowHeader { get; private set; } = "Overflow mode";

    /// <summary>Why loading a workspace schema failed; null when it did not.</summary>
    [ObservableProperty]
    public partial string? SchemaError { get; private set; }

    /// <summary>The window's .nnschema file picker; null where there is none (tests, headless).</summary>
    public Func<Task<string?>>? PickSchemaFile { get; set; }

    /// <summary>The open documents, the active one and the sync bus.</summary>
    public DocumentsViewModel Documents { get; }

    /// <summary>The title bar and the window's collapse state (OQ10).</summary>
    public ShellChromeViewModel Chrome { get; }

    /// <summary>The Inspect workspace: timeline, stream panes and inspector over the active document.</summary>
    public InspectWorkspaceViewModel Inspect { get; }

    /// <summary>The Compare workspace: the two ticked documents aligned, their entity union and differences.</summary>
    public CompareWorkspaceViewModel Compare { get; }

    /// <summary>The workspace splitters' sizes, kept in the settings.</summary>
    public PaneSizesViewModel Panes { get; }

    /// <summary>The session-ended strip under the title bar (screens/session-ended).</summary>
    public SessionEndedViewModel Ended { get; }

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

        // A server's session (of several) is picked in the title bar's session popover, as after attaching.
        ConnectDialogViewModel dialog = new(connect, CloseDialog) { SessionChooser = (sessions, cancel) => Chrome.Session.ChooseAsync(connect, sessions, cancel) };
        Dialog = dialog;
        return dialog;
    }

    /// <summary>F1, Help › Help &amp; shortcuts: the command and gesture list (unless a dialog is already open).</summary>
    /// <returns>The dialog; null when another dialog was open.</returns>
    public HelpDialogViewModel? ShowHelp()
    {
        if (Dialog is not null) return null;
        HelpDialogViewModel dialog = new();
        dialog.Closed += d =>
        {
            if (ReferenceEquals(Dialog, d)) Dialog = null;
        };
        Dialog = dialog;
        return dialog;
    }

    /// <summary>F2, View › Legend &amp; symbols: what every chip, fill and mark means (unless a dialog is already open).</summary>
    /// <returns>The dialog; null when another dialog was open.</returns>
    public LegendDialogViewModel? ShowLegend()
    {
        if (Dialog is not null) return null;
        LegendDialogViewModel dialog = new();
        dialog.Closed += d =>
        {
            if (ReferenceEquals(Dialog, d)) Dialog = null;
        };
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

    /// <summary>
    /// Ctrl+Shift+S, File › Export section as .nntl…, and the timeline, hex and row menus: the export dialog over the
    /// active document (unless a dialog is already open); it closes itself.
    /// </summary>
    /// <param name="initial">The range the dialog starts on; null: the document's scope when it has one, else the time window.</param>
    /// <returns>The dialog; null when another dialog was open or the active document holds nothing.</returns>
    public ExportSectionDialogViewModel? ShowExportSection(ExportRangeKind? initial = null)
    {
        if (Dialog is not null || Documents.Active is not { Session: not null } document) return null;
        ExportSectionDialogViewModel dialog = new(Documents, document, initial);
        dialog.Closed += d =>
        {
            if (ReferenceEquals(Dialog, d)) Dialog = null;
        };
        Dialog = dialog;
        return dialog;
    }

    /// <summary>
    /// Ctrl+Shift+F, View › Filter to time range…, the palette: the scope dialog over the active document (unless a dialog
    /// is already open); it closes itself.
    /// </summary>
    /// <returns>The dialog; null when another dialog was open or the active document holds nothing to scope.</returns>
    public ScopeDialogViewModel? ShowScopeDialog()
    {
        if (Dialog is not null || !Documents.CanScope || Documents.Active is not { } document) return null;
        ScopeDialogViewModel dialog = new(Documents, document);
        dialog.Closed += d =>
        {
            if (ReferenceEquals(Dialog, d)) Dialog = null;
        };
        Dialog = dialog;
        return dialog;
    }

    /// <summary>Ctrl+S, File › Save timeline as .nntl…: saves the active document through the documents' save handler (I29).</summary>
    private async void Save()
    {
        if (Documents.Active is not { } document || Documents.SaveHandler is not { } save) return;
        try
        {
            await save(document).ConfigureAwait(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // An async void command must not throw into the dispatcher; the save handler reports its own failures.
            System.Diagnostics.Debug.WriteLine("save failed: " + e.Message);
        }
    }

    /// <summary>Save .nntl… through the window's save picker (the documents' save handler).</summary>
    private async Task<bool> SaveDocumentAsync(DocumentViewModel document) => await Documents.SaveAsync(document).ConfigureAwait(true) is not null;

    /// <summary>
    /// The unsaved-data prompt (OQ5) as the documents' close prompt: the tab in question comes to the front, then Save
    /// .nntl… / Discard / Cancel. Another open dialog answers Cancel, so nothing is lost behind it.
    /// </summary>
    private Task<CloseDecision> PromptUnsaved(DocumentViewModel document)
    {
        if (Dialog is not null) return Task.FromResult(CloseDecision.Cancel);
        if (Documents.Documents.Contains(document)) Documents.Activate(document);
        UnsavedCloseDialogViewModel dialog = new(document);
        dialog.Closed += d =>
        {
            if (ReferenceEquals(Dialog, d)) Dialog = null;
        };
        Dialog = dialog;
        return dialog.Result;
    }

    /// <summary>True when a tab holds captured data that is not saved (closing the window asks first).</summary>
    public bool HasUnsavedDocuments
    {
        get
        {
            foreach (DocumentViewModel document in Documents.Documents)
            {
                if (document.IsDirty) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Before the window closes (OQ5): each tab with unsaved data in turn comes to the front and asks Save .nntl… /
    /// Discard / Cancel (&lt;see cref="DocumentsViewModel.ConfirmDiscardAsync"/&gt;). A dialog already open keeps the window:
    /// it is answered first, so nothing is lost behind it.
    /// </summary>
    /// <returns>True when the window may close: every unsaved tab was saved or discarded.</returns>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!HasUnsavedDocuments) return true;
        if (Dialog is not null) return false;
        foreach (DocumentViewModel document in Documents.Documents.ToArray())
        {
            if (document.IsDirty && !await Documents.ConfirmDiscardAsync(document).ConfigureAwait(true)) return false;
        }

        return true;
    }

    /// <summary>File › Recent ▸ item: opens the recording.</summary>
    [RelayCommand]
    private Task OpenRecent(string path) => OpenRecordingAsync(path);

    /// <summary>Session › Schema ▸ › Load workspace schema…: a .nnschema the documents opened from now on compare with and can decode by.</summary>
    [RelayCommand]
    private async Task LoadSchema()
    {
        SchemaError = null;
        if (PickSchemaFile is not { } pick || await pick().ConfigureAwait(true) is not { } path) return;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            if (!NexusSchema.TryRead(bytes, out NexusSchema? schema, out string? error))
            {
                SchemaError = $"{Path.GetFileName(path)} is not a schema: {error}";
                return;
            }

            Documents.WorkspaceSchema = schema;
            Documents.WorkspaceSchemaName = path;
            RefreshAppMenu();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SchemaError = $"Could not read {Path.GetFileName(path)}: {e.Message}";
        }
    }

    /// <summary>Session › Schema ▸ › Clear workspace schema: recordings decode by their embedded schema again.</summary>
    [RelayCommand]
    private void ClearSchema()
    {
        Documents.WorkspaceSchema = null;
        Documents.WorkspaceSchemaName = null;
        RefreshAppMenu();
    }

    /// <summary>Session › Overflow mode ▸: what later attaches do when the viewer falls behind.</summary>
    [RelayCommand]
    private void SetOverflow(InspectorOverflow overflow)
    {
        Documents.SetOverflow(overflow);
        RefreshAppMenu();
    }

    /// <summary>"Chat.nnschema (embedded)", "contracts.nnschema (workspace)", "raw (no schema)".</summary>
    public static string SchemaLabel(DocumentViewModel? active, NexusSchema? workspace, string? workspaceName)
    {
        if (active?.Session is { } session)
        {
            return session.Schema.Source switch
            {
                SchemaSource.Embedded => SchemaName(session.Schema.Schema) + " (embedded)",
                SchemaSource.Workspace => WorkspaceLabel(workspace ?? session.Schema.Schema, workspaceName) + " (workspace)",
                _ => "raw (no schema)",
            };
        }

        return workspace is null ? "none" : WorkspaceLabel(workspace, workspaceName) + " (workspace)";
    }

    // The nexus's name without its Nexus suffix, as a file name ("ChatServerNexus" -> "Chat.nnschema").
    private static string SchemaName(NexusSchema? schema)
    {
        if (schema is not { Exports.Count: > 0 }) return "schema";
        string name = schema.Exports[0].Name;
        foreach (string suffix in (ReadOnlySpan<string>)["ServerNexus", "ClientNexus", "Nexus"])
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return name + ".nnschema";
    }

    private static string WorkspaceLabel(NexusSchema? schema, string? path) => path is { Length: > 0 } ? Path.GetFileName(path) : SchemaName(schema);

    /// <summary>Rebuilds the app menu's submenus and headers from the documents, settings and live sessions.</summary>
    public void RefreshAppMenu()
    {
        Commands.Refresh();
        AppSettings settings = _store?.Current ?? new AppSettings();
        MenuChoice[] recent = new MenuChoice[settings.RecentFiles.Count];
        for (int i = 0; i < recent.Length; i++)
            recent[i] = new MenuChoice(Path.GetFileName(settings.RecentFiles[i].Path), OpenRecentCommand, settings.RecentFiles[i].Path);
        RecentItems = recent;

        SchemaHeader = "Schema · " + SchemaLabel(Documents.Active, Documents.WorkspaceSchema, Documents.WorkspaceSchemaName);
        SchemaItems =
        [
            new MenuChoice("Load workspace schema…", LoadSchemaCommand),
            new MenuChoice("Clear workspace schema", ClearSchemaCommand, IsEnabled: Documents.WorkspaceSchema is not null),
        ];

        InspectorOverflow overflow = Documents.ConnectDefaults.Overflow;
        OverflowHeader = "Overflow mode · " + (overflow == InspectorOverflow.Block ? "Block" : "Drop");
        OverflowItems =
        [
            new MenuChoice("Drop · show gap markers", SetOverflowCommand, InspectorOverflow.Drop, overflow == InspectorOverflow.Drop),
            new MenuChoice("Block · lossless, slows the connection", SetOverflowCommand, InspectorOverflow.Block, overflow == InspectorOverflow.Block),
        ];
        RefreshSessionItems();
    }

    private void RefreshSessionItems()
    {
        SessionAreaViewModel area = Chrome.Session;
        MenuChoice[] sessions = new MenuChoice[area.Sessions.Count];
        for (int i = 0; i < sessions.Length; i++)
        {
            SessionChoice choice = area.Sessions[i];
            sessions[i] = new MenuChoice(choice.MenuLabel, area.ChooseCommand, choice, choice.IsAttached);
        }

        SessionItems = sessions;
    }

    partial void OnIsAppMenuOpenChanged(bool value)
    {
        if (!value) return;
        RefreshAppMenu();
        _ = Chrome.Session.RefreshSessionsAsync();
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsAppMenuOpen) RefreshSessionItems();
    }

    /// <summary>The title bar's requests: the logo's app menu, its Connect pill and the tab strip's +.</summary>
    private void OnShellRequested(ShellRequest request)
    {
        switch (request)
        {
            case ShellRequest.AppMenu:
                IsAppMenuOpen = !IsAppMenuOpen;
                break;
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
        Commands.Bind(AppCommand.OpenInCompare, Compare.Show, () => Dialog is null && Documents.Documents.Count >= DocumentsViewModel.MaxCompared);
        Commands.Bind(AppCommand.NextDocument, () => Documents.Cycle(backwards: false), () => Documents.Documents.Count > 1);
        Commands.Bind(AppCommand.PreviousDocument, () => Documents.Cycle(backwards: true), () => Documents.Documents.Count > 1);
        Commands.Bind(AppCommand.ThemeDark, () => Theme = AppTheme.Dark);
        Commands.Bind(AppCommand.ThemeLight, () => Theme = AppTheme.Light);
        Commands.Bind(AppCommand.ThemeSystem, () => Theme = AppTheme.System);
        Commands.Bind(AppCommand.SaveTimeline, Save, () => Dialog is null && Documents.Active?.Session is not null && Documents.SaveHandler is not null);
        SessionAreaViewModel session = Chrome.Session;
        Commands.Bind(AppCommand.RecordAll, () => session.RecordAllCommand.Execute(null), () => session.RecordAllCommand.CanExecute(null));
        session.RecordAllCommand.CanExecuteChanged += (_, _) => Commands.Refresh();
        Commands.Bind(AppCommand.HelpShortcuts, () => ShowHelp(), () => Dialog is null);
        Commands.Bind(AppCommand.Legend, () => ShowLegend(), () => Dialog is null);
        Commands.Bind(AppCommand.ExportSelection, () => ShowExportSection(),
            () => Dialog is null && Documents.Active?.Session is not null && Documents.SavePicker is not null);
        Commands.Bind(AppCommand.Detach, () => session.DetachCommand.Execute(null), () => session.DetachCommand.CanExecute(null));
        Commands.Bind(AppCommand.FilterToRange, () => ShowScopeDialog(), () => Dialog is null && Documents.CanScope);
        Commands.Bind(AppCommand.FilterToView, () => Documents.FilterToView(), () => Dialog is null && Documents.CanScope && !Documents.Sync.Window.IsEmpty);
        Commands.Bind(AppCommand.ScopeBack, () => Documents.PopScope(), () => Dialog is null && Documents.Active is { IsScoped: true });
        Commands.Bind(AppCommand.ClearScope, () => Documents.ClearScope(), () => Dialog is null && Documents.Active is { IsScoped: true });
        session.DetachCommand.CanExecuteChanged += (_, _) => Commands.Refresh();
        BookmarksViewModel bookmarks = Chrome.Bookmarks;
        Commands.Bind(AppCommand.AddBookmark, bookmarks.Add, () => Dialog is null && Documents.CanBookmark);
        for (int n = 1; n <= 9; n++)
        {
            int index = n - 1;
            Commands.Bind(CommandRegistry.JumpBookmark(n), () => Documents.JumpToBookmark(index),
                () => Dialog is null && Documents.ActiveBookmarks.Count > index);
        }

        Documents.Bookmarks.Changed += _ => Commands.Refresh();
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
        ThemeChoice.Value = value;
        _themes.Theme = value;
        _store?.Update(s => s with { Theme = value });
    }
}
