using Avalonia.Input;
using NexNet.Inspector.App.Services.Commands;

namespace NexNet.Inspector.App.Tests.Palette;

/// <summary>The command registry the palette and the app menu share: one definition per command, handlers bound by owners.</summary>
public sealed class CommandRegistryTests
{
    [Test]
    public async Task Every_command_has_one_definition_in_enum_order_with_a_title()
    {
        AppCommand[] commands = Enum.GetValues<AppCommand>().Where(c => c != AppCommand.None).ToArray();

        await Assert.That(CommandRegistry.Definitions.Count).IsEqualTo(commands.Length);
        for (int i = 0; i < commands.Length; i++)
        {
            await Assert.That(CommandRegistry.Definitions[i].Command).IsEqualTo(commands[i]);
            await Assert.That(CommandRegistry.Get(commands[i]).Title.Length).IsGreaterThan(0);
        }

        await Assert.That(() => CommandRegistry.Get(AppCommand.None)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Shortcuts_are_unique_and_only_modified_or_function_keys_bind_window_wide()
    {
        KeyGesture[] gestures = CommandRegistry.Definitions.Where(d => d.Gesture is not null).Select(d => d.Gesture!).ToArray();

        await Assert.That(gestures.Distinct().Count()).IsEqualTo(gestures.Length);
        await Assert.That(CommandRegistry.Get(AppCommand.CommandPalette).ShortcutText).IsEqualTo("Ctrl+Shift+P");
        await Assert.That(CommandRegistry.Get(AppCommand.CommandPalette).IsWindowShortcut).IsTrue();
        await Assert.That(CommandRegistry.Get(AppCommand.GoToTime).IsWindowShortcut).IsTrue();
        await Assert.That(CommandRegistry.Get(AppCommand.HelpShortcuts).IsWindowShortcut).IsTrue();
        // A bare letter belongs to the view under the pointer; Ctrl+Tab to the window's own document cycling.
        await Assert.That(CommandRegistry.Get(AppCommand.TraceMessage).ShortcutText).IsEqualTo("T");
        await Assert.That(CommandRegistry.Get(AppCommand.TraceMessage).IsWindowShortcut).IsFalse();
        await Assert.That(CommandRegistry.Get(AppCommand.NextDocument).IsWindowShortcut).IsFalse();
        await Assert.That(CommandRegistry.Get(AppCommand.OpenInCompare).ShortcutText).IsNull();
    }

    [Test]
    public async Task An_unbound_command_is_listed_but_cannot_run()
    {
        CommandRegistry registry = new();

        await Assert.That(registry.IsBound(AppCommand.ExportSelection)).IsFalse();
        await Assert.That(registry.CanExecute(AppCommand.ExportSelection)).IsFalse();
        await Assert.That(registry.Execute(AppCommand.ExportSelection)).IsFalse();
        await Assert.That(registry.CommandFor(AppCommand.ExportSelection).CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task A_bound_command_runs_when_its_condition_holds_and_its_ICommand_follows_Refresh()
    {
        CommandRegistry registry = new();
        bool allowed = false;
        int ran = 0, changed = 0, notified = 0;
        registry.Changed += () => changed++;
        registry.Bind(AppCommand.GoToTime, () => ran++, () => allowed);
        CommunityToolkit.Mvvm.Input.IRelayCommand command = registry.CommandFor(AppCommand.GoToTime);
        command.CanExecuteChanged += (_, _) => notified++;

        await Assert.That(registry.Execute(AppCommand.GoToTime)).IsFalse();
        await Assert.That(command.CanExecute(null)).IsFalse();
        allowed = true;
        registry.Refresh();
        await Assert.That(notified).IsEqualTo(1);
        await Assert.That(command.CanExecute(null)).IsTrue();
        command.Execute(null);
        await Assert.That(registry.Execute(AppCommand.GoToTime)).IsTrue();
        await Assert.That(ran).IsEqualTo(2);
        await Assert.That(changed).IsEqualTo(2);
        await Assert.That(registry.CommandFor(AppCommand.GoToTime)).IsSameReferenceAs(command);
    }

    [Test]
    public async Task Suggestions_are_the_packages_command_list()
    {
        await Assert.That(CommandRegistry.Suggestions.ToArray()).IsEquivalentTo(
            new[] { AppCommand.TraceMessage, AppCommand.GoToTime, AppCommand.OpenInCompare, AppCommand.ExportSelection },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
