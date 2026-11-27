using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Services.Commands;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>One line of the Help &amp; shortcuts list: a section heading, or an action and its keys.</summary>
/// <param name="Text">The heading or the action.</param>
/// <param name="Keys">The keys or pointer gesture; empty for a heading or a command without a shortcut.</param>
/// <param name="IsHeading">True for a section heading.</param>
public readonly record struct HelpRow(string Text, string Keys, bool IsHeading);

/// <summary>
/// Help &amp; shortcuts (F1): every command of the registry with its shortcut, by app-menu section, then the keys and
/// pointer gestures the views handle themselves (they are not commands, so the registry cannot list them). Enter and Esc
/// close it.
/// </summary>
public sealed partial class HelpDialogViewModel
{
    /// <summary>What the timeline, ladder, hex view and tabs do on their own (each view documents its handling).</summary>
    private static readonly (string Action, string Keys)[] ViewGestures =
    [
        ("Zoom the time window around the pointer", "Ctrl+Wheel"),
        ("Pan the time window", "Wheel"),
        ("Scroll the timeline's lanes", "Shift+Wheel"),
        ("Step to the previous / next frame", "← / →"),
        ("Pin the lane under the pointer", "P"),
        ("Trace the message under the pointer", "T"),
        ("Trace the selected ladder row's message", "Enter / double-click"),
        ("Close a document tab", "Middle-click"),
        ("Open a compared entity in Inspect", "Double-click"),
    ];

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HelpDialogViewModel() => Rows = Build();

    /// <summary>Raised once closed: the host removes the dialog.</summary>
    public event Action<HelpDialogViewModel>? Closed;

    public string Title => "Help & shortcuts";

    public string Hint => "Esc to close · Ctrl+Shift+P runs any of these by name";

    /// <summary>The list, headings included.</summary>
    public IReadOnlyList<HelpRow> Rows { get; }

    /// <summary>Completes when the dialog closes.</summary>
    public Task Result => _closed.Task;

    /// <summary>The rows: commands by app-menu section in registry order, then the views' own gestures.</summary>
    public static HelpRow[] Build()
    {
        List<HelpRow> rows = [];
        for (CommandGroup group = CommandGroup.File; group <= CommandGroup.Help; group++)
        {
            bool headed = false;
            foreach (CommandDefinition definition in CommandRegistry.Definitions)
            {
                if (definition.Group != group) continue;
                if (!headed)
                {
                    rows.Add(new HelpRow(GroupName(group), "", IsHeading: true));
                    headed = true;
                }

                rows.Add(new HelpRow(definition.Title, definition.ShortcutText ?? "", IsHeading: false));
            }
        }

        rows.Add(new HelpRow("In the views", "", IsHeading: true));
        foreach ((string action, string keys) in ViewGestures) rows.Add(new HelpRow(action, keys, IsHeading: false));
        return [.. rows];
    }

    private static string GroupName(CommandGroup group) => group switch
    {
        CommandGroup.File => "File",
        CommandGroup.Session => "Session",
        CommandGroup.View => "View",
        _ => "Help",
    };

    /// <summary>Enter, Esc and Close.</summary>
    [RelayCommand]
    private void Close()
    {
        if (_closed.Task.IsCompleted) return;
        _closed.TrySetResult();
        Closed?.Invoke(this);
    }
}
