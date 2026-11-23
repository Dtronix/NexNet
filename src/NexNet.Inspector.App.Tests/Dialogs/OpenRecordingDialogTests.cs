using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.OpenRecording;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;

namespace NexNet.Inspector.App.Tests.Dialogs;

/// <summary>The open-recording dialog's model: session list, info strip, schema choice, damaged and unreadable files.</summary>
public sealed class OpenRecordingDialogTests
{
    [Test]
    public async Task A_legacy_recording_shows_the_schema_mismatch_and_opens_the_chosen_session_and_schema()
    {
        using RecordingFile recording = RecordingFile.Open(Wait.Fixture("legacy-schema.nntl"));
        OpenRecordingDialogViewModel? closed = null;
        OpenRecordingDialogViewModel dialog = new(recording, NexusSchema.Of<ChatServerNexus>(), "Chat.nnschema", TimeZoneInfo.Utc, d => closed = d);

        await Assert.That(dialog.Rows.Count).IsEqualTo(2);
        await Assert.That(dialog.SelectedIndex).IsEqualTo(0);
        await Assert.That(dialog.Source.Picked).IsEqualTo(0);
        await Assert.That(dialog.PrimaryLabel).IsEqualTo("Open session " + LiveDocumentViewModel.SessionHex(recording.Sessions[0].SessionId));
        await Assert.That(dialog.SchemaStatus).IsEqualTo(SchemaStatus.Mismatch);
        await Assert.That(dialog.ShowsSchemaChoice).IsTrue();
        await Assert.That(dialog.SchemaTitle).IsEqualTo("The embedded schema differs from your workspace schema");
        await Assert.That(dialog.SchemaDetail.Any(p => p.Mono && p.Text == "Chat.nnschema")).IsTrue();
        await Assert.That(dialog.SchemaDetail.Any(p => p.Mono && p.Text == RecordingText.Hash(NexusSchema.Of<ChatServerNexus>().Hash))).IsTrue();
        await Assert.That(dialog.UsesEmbedded).IsTrue();
        await Assert.That(dialog.HasDamage).IsFalse();

        dialog.SelectedIndex = 1;
        dialog.UsesRaw = true;
        await Assert.That(dialog.Source.Picked).IsEqualTo(1);
        await Assert.That(dialog.UsesEmbedded).IsFalse();
        dialog.OpenCommand.Execute(null);

        RecordingOpenChoice? result = await dialog.Result;
        await Assert.That(result).IsEqualTo(new RecordingOpenChoice(recording.Sessions[1].SessionId, SchemaChoice.Raw));
        await Assert.That(closed).IsSameReferenceAs(dialog);
        await Assert.That(dialog.IsClosed).IsTrue();
        await Assert.That(dialog.OpenCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task A_matching_multi_session_recording_lists_its_sessions_and_info_and_Cancel_answers_null()
    {
        using RecordingFile recording = RecordingFile.Open(Wait.Fixture("server-all.nntl"));
        int closes = 0;
        OpenRecordingDialogViewModel dialog = new(recording, null, null, TimeZoneInfo.Utc, _ => closes++);

        await Assert.That(dialog.FileName).IsEqualTo("server-all.nntl");
        await Assert.That(dialog.Rows.Count).IsEqualTo(3);
        await Assert.That(dialog.RecordedBy).IsEqualTo("server capture");
        await Assert.That(dialog.Host).IsEqualTo(recording.Header.Host);
        await Assert.That(dialog.SessionCount).IsEqualTo("3 sessions");
        await Assert.That(dialog.Gaps + dialog.GapsNoun).IsEqualTo("0 gaps");
        await Assert.That(dialog.Size).IsEqualTo(RecordingText.Size(recording.Length));
        await Assert.That(dialog.Started).IsEqualTo(recording.Header.ToUtc(recording.Sessions.Min(s => s.Ticks)).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        for (int i = 0; i < 3; i++)
        {
            RecordedSessionSummary summary = recording.Summarize(recording.Sessions[i]);
            await Assert.That(dialog.Rows[i].SessionId).IsEqualTo(summary.Open.SessionId);
            await Assert.That(dialog.Rows[i].Events).IsEqualTo(RecordingText.Count(summary.Ops));
            await Assert.That(dialog.Rows[i].Transport).IsEqualTo("TCP");
            await Assert.That(dialog.Rows[i].Remote).StartsWith(summary.Open.RemoteEndpoint!);
        }

        await Assert.That(dialog.Rows[0].Start).IsEqualTo("00:00.0");
        await Assert.That(dialog.TableHeight).IsEqualTo(20 + 1 + (3 * 27) + 1);
        await Assert.That(dialog.SchemaStatus).IsEqualTo(SchemaStatus.Embedded);
        await Assert.That(dialog.HasSchemaBanner).IsFalse();
        await Assert.That(dialog.ShowsSchemaChoice).IsFalse();
        await Assert.That(dialog.HasBody).IsFalse();
        await Assert.That(dialog.CompareCommand.CanExecute(null)).IsFalse();

        dialog.CancelCommand.Execute(null);
        await Assert.That(await dialog.Result).IsNull();
        dialog.CancelCommand.Execute(null);
        await Assert.That(closes).IsEqualTo(1);
    }

    [Test]
    public async Task A_truncated_recording_shows_the_damage_card_naming_the_repair_command_and_still_lists_every_session()
    {
        using RecordingFile recording = RecordingFile.Open(Wait.Fixture("server-all-truncated.nntl"));
        OpenRecordingDialogViewModel dialog = new(recording, null, null, TimeZoneInfo.Utc, static _ => { });

        await Assert.That(dialog.HasDamage).IsTrue();
        await Assert.That(dialog.HasBody).IsTrue();
        await Assert.That(dialog.DamageTitle).IsEqualTo("Parts of this recording could not be read");
        string text = string.Concat(dialog.DamageDetail.Select(p => p.Text));
        await Assert.That(text).Contains("index footer is missing");
        await Assert.That(text).Contains("1 damaged region (18 bytes) was skipped");
        await Assert.That(dialog.DamageDetail.Any(p => p.Mono && p.Text == "nexnet-tap repair server-all-truncated.nntl")).IsTrue();
        await Assert.That(dialog.Rows.Count).IsEqualTo(3);
        // The session whose close block was cut off says so.
        await Assert.That(dialog.Rows.Count(r => r.Remote.EndsWith("recording ended first", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task Compare_two_sessions_hands_the_recording_over_and_closes_when_Compare_takes_it()
    {
        using RecordingFile recording = RecordingFile.Open(Wait.Fixture("server-all.nntl"));
        RecordingFile? handed = null;
        OpenRecordingDialogViewModel dialog = new(recording, null, null, TimeZoneInfo.Utc, static _ => { }, r =>
        {
            handed = r;
            return Task.FromResult(true);
        });

        await Assert.That(dialog.CompareCommand.CanExecute(null)).IsTrue();
        await dialog.CompareCommand.ExecuteAsync(null);
        await Assert.That(handed).IsSameReferenceAs(recording);
        await Assert.That(await dialog.Result).IsNull();
        await Assert.That(dialog.IsClosed).IsTrue();
    }

    [Test]
    public async Task The_method_change_counts_methods_added_and_removed_between_the_schemas()
    {
        using RecordingFile legacy = RecordingFile.Open(Wait.Fixture("legacy-schema.nntl"));
        NexusSchema recorded = legacy.GetEmbeddedSchema(legacy.Sessions[0])!;
        NexusSchema current = NexusSchema.Of<ChatServerNexus>();

        string added = OpenRecordingDialogViewModel.MethodChange(recorded, current);
        string removed = OpenRecordingDialogViewModel.MethodChange(current, recorded);
        await Assert.That(added).EndsWith("added");
        await Assert.That(removed).EndsWith("removed");
        await Assert.That(added.Split(' ')[0]).IsEqualTo(removed.Split(' ')[0]);
        await Assert.That(OpenRecordingDialogViewModel.MethodChange(current, current)).IsEqualTo("same methods, different signatures");
    }

    [Test]
    public async Task A_file_that_cannot_be_read_gets_the_error_state()
    {
        int closes = 0;
        OpenRecordingDialogViewModel dialog = OpenRecordingDialogViewModel.Failed("/captures/broken.nntl", "not a recording", _ => closes++);

        await Assert.That(dialog.IsFailed).IsTrue();
        await Assert.That(dialog.FailureText).IsEqualTo("Could not read broken.nntl: not a recording");
        await Assert.That(dialog.CancelLabel).IsEqualTo("Close");
        await Assert.That(dialog.HasBody).IsTrue();
        await Assert.That(dialog.Rows.Count).IsEqualTo(0);
        await Assert.That(dialog.OpenCommand.CanExecute(null)).IsFalse();
        dialog.CancelCommand.Execute(null);
        await Assert.That(await dialog.Result).IsNull();
        await Assert.That(closes).IsEqualTo(1);
    }

    [Test]
    public async Task Recording_text_formats_sizes_spans_offsets_and_lengths()
    {
        await Assert.That(RecordingText.Size(512)).IsEqualTo("512 B");
        await Assert.That(RecordingText.Size(17_166)).IsEqualTo("16.8 KB");
        await Assert.That(RecordingText.Size(43_200_000)).IsEqualTo("41.2 MB");
        await Assert.That(RecordingText.Span(472, 1000)).IsEqualTo("472 ms");
        await Assert.That(RecordingText.Span(12_400, 1000)).IsEqualTo("12.4 s");
        await Assert.That(RecordingText.Span(300_000, 1000)).IsEqualTo("5 m 00 s");
        await Assert.That(RecordingText.Span(3_900_000, 1000)).IsEqualTo("1 h 05 m");
        await Assert.That(RecordingText.Offset(41_300, 1000)).IsEqualTo("00:41.3");
        await Assert.That(RecordingText.Offset(192_900, 1000)).IsEqualTo("03:12.9");
        await Assert.That(RecordingText.Length(258_700, 1000)).IsEqualTo("4:18.7");
        await Assert.That(RecordingText.Length(400, 1000)).IsEqualTo("0:00.4");
        await Assert.That(RecordingText.Length(59_960, 1000)).IsEqualTo("1:00.0");
        await Assert.That(RecordingText.Count(18_248)).IsEqualTo("18,248");
        await Assert.That(RecordingText.Plural(1, "gap")).IsEqualTo("1 gap");
        await Assert.That(RecordingText.Hash(0xB144AAE0007D23DF)).IsEqualTo("0xB144AAE0007D23DF");
    }

    [Test]
    public async Task The_picker_is_asked_for_a_single_session_only_when_its_schema_or_file_needs_a_choice()
    {
        using RecordingFile legacy = RecordingFile.Open(Wait.Fixture("legacy-schema.nntl"));
        NexusSchema old = legacy.GetEmbeddedSchema(legacy.Sessions[0])!;
        TestUi ui = new();
        await using DocumentsViewModel documents = new(ui, null);
        int asked = 0;
        documents.SessionPicker = recording =>
        {
            asked++;
            return Task.FromResult<RecordingOpenChoice?>(new RecordingOpenChoice(recording.Sessions[0].SessionId, SchemaChoice.Raw));
        };

        // One session whose schema matches (no workspace schema to differ from): opens without asking.
        FileDocumentViewModel plain = (await documents.OpenRecordingAsync(Wait.Fixture("chat-server.nntl")))!;
        await plain.Indexing;
        await Assert.That(asked).IsEqualTo(0);
        await Wait.UntilAsync(() => plain.SchemaSource == SchemaSource.Embedded, "the embedded schema");

        // The same file against an older workspace schema: the dialog is asked, and its choice decodes.
        documents.WorkspaceSchema = old;
        FileDocumentViewModel mismatched = (await documents.OpenRecordingAsync(Wait.Fixture("chat-server.nntl")))!;
        await mismatched.Indexing;
        await Assert.That(asked).IsEqualTo(1);
        await Wait.UntilAsync(() => mismatched.SchemaSource == SchemaSource.None && mismatched.FrameCount > 0, "raw decoding");

        using RecordingFile truncated = RecordingFile.Open(Wait.Fixture("server-all-truncated.nntl"));
        documents.WorkspaceSchema = null;
        await Assert.That(documents.NeedsPicker(truncated)).IsTrue();
        await Assert.That(documents.NeedsPicker(legacy)).IsTrue();
    }
}

/// <summary>The open-recording dialog in the main window: the open-recording screen, keyboard, and the error state.</summary>
[NotInParallel("avalonia")]
public sealed class OpenRecordingScreenTests
{
    [Test]
    public async Task The_screen_shows_the_720_px_dialog_and_the_keyboard_picks_a_session_and_opens_it()
    {
        (double width, int rows, bool banner, bool choice, bool tableFocused, int picked, string label, long opened, SchemaSource source, long expected) =
            await HeadlessSession.Run(() =>
            {
                using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
                MainWindowViewModel viewModel = OpenRecordingScreen.Model(themes);
                MainWindow window = new() { DataContext = viewModel };
                window.Show();
                try
                {
                    (OpenRecordingDialogViewModel dialog, Task opening) = OpenRecordingScreen.Open(viewModel);
                    UiPump.Frames();
                    OpenRecordingDialog view = window.FindControl<DialogHost>("Dialogs")!.GetVisualDescendants().OfType<OpenRecordingDialog>().Single();
                    DataTableControl table = view.FindControl<DataTableControl>("Sessions")!;
                    bool focused = window.FocusManager?.GetFocusedElement() is Visual focus && (ReferenceEquals(focus, table) || table.IsVisualAncestorOf(focus));
                    double w = view.FindControl<DialogFrame>("Frame")!.Bounds.Width;
                    int realized = table.RealizedCount;
                    bool bannerShown = view.FindControl<Banner>("SchemaBanner")!.IsEffectivelyVisible;
                    bool choiceShown = view.FindControl<StackPanel>("SchemaChoice")!.IsEffectivelyVisible;

                    window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                    UiPump.Frames();
                    int pick = dialog.SelectedIndex;
                    string primary = dialog.PrimaryLabel;
                    view.FindControl<RadioButton>("WorkspaceRow")!.IsChecked = true;
                    window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                    InspectLiveScreen.Pump(() => opening.IsCompleted);
                    FileDocumentViewModel document = (FileDocumentViewModel)viewModel.Documents.Active!;
                    long id = document.Session!.SessionId;
                    SchemaSource decodes = document.SchemaSource;
                    long second = document.Recording.Sessions[1].SessionId;
                    Task disposing = viewModel.Documents.DisposeAsync().AsTask();
                    InspectLiveScreen.Pump(() => disposing.IsCompleted);
                    return (w, realized, bannerShown, choiceShown, focused, pick, primary, id, decodes, second);
                }
                finally
                {
                    window.Close();
                }
            });

        await Assert.That(width).IsEqualTo(720);
        await Assert.That(rows).IsEqualTo(2);
        await Assert.That(banner).IsTrue();
        await Assert.That(choice).IsTrue();
        await Assert.That(tableFocused).IsTrue();
        await Assert.That(picked).IsEqualTo(1);
        await Assert.That(label).IsEqualTo("Open session " + LiveDocumentViewModel.SessionHex(expected));
        await Assert.That(opened).IsEqualTo(expected);
        await Assert.That(source).IsEqualTo(SchemaSource.Workspace);
    }

    [Test]
    public async Task Esc_cancels_and_opens_nothing()
    {
        (bool closed, bool documents) = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Dark);
            MainWindowViewModel viewModel = OpenRecordingScreen.Model(themes);
            MainWindow window = new() { DataContext = viewModel };
            window.Show();
            try
            {
                (_, Task opening) = OpenRecordingScreen.Open(viewModel, "server-all.nntl");
                UiPump.Frames();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                InspectLiveScreen.Pump(() => opening.IsCompleted);
                return (viewModel.Dialog is null && !window.FindControl<DialogHost>("Dialogs")!.IsOpen, viewModel.Documents.HasDocuments);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(closed).IsTrue();
        await Assert.That(documents).IsFalse();
    }

    [Test]
    public async Task An_unreadable_file_opens_the_dialog_in_its_error_state()
    {
        using TempDirectory dir = new();
        string path = dir.File("broken.nntl");
        await File.WriteAllBytesAsync(path, new byte[64]);

        (bool failed, bool statusShown, bool openShown, string? text, bool closedByEsc) = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
            MainWindowViewModel viewModel = new(themes, null);
            MainWindow window = new() { DataContext = viewModel };
            window.Show();
            try
            {
                Task opening = viewModel.OpenRecordingAsync(path);
                InspectLiveScreen.Pump(() => opening.IsCompleted);
                UiPump.Frames();
                OpenRecordingDialogViewModel? dialog = viewModel.Dialog as OpenRecordingDialogViewModel;
                OpenRecordingDialog view = window.FindControl<DialogHost>("Dialogs")!.GetVisualDescendants().OfType<OpenRecordingDialog>().Single();
                bool status = view.FindControl<Banner>("FailureStatus")!.IsEffectivelyVisible;
                bool open = view.FindControl<Button>("OpenButton")!.IsEffectivelyVisible;
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiPump.Frames();
                return (dialog?.IsFailed ?? false, status, open, dialog?.FailureText, viewModel.Dialog is null);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(failed).IsTrue();
        await Assert.That(statusShown).IsTrue();
        await Assert.That(openShown).IsFalse();
        await Assert.That(text).StartsWith("Could not read broken.nntl: ");
        await Assert.That(closedByEsc).IsTrue();
    }
}
