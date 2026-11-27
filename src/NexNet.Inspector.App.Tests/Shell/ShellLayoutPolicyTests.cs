using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>OQ10's collapse rules as a pure policy, the chrome mode per platform, and the chrome view-model that carries both.</summary>
public sealed class ShellLayoutPolicyTests
{
    [Test]
    [Arguments(1440d, ShellLayoutState.Full)]
    [Arguments(1200d, ShellLayoutState.Full)]
    [Arguments(1199.5d, ShellLayoutState.RightPaneClosed)]
    [Arguments(1150d, ShellLayoutState.RightPaneClosed)]
    [Arguments(1149d, ShellLayoutState.InspectorNarrow)]
    [Arguments(1100d, ShellLayoutState.InspectorNarrow)]
    [Arguments(900d, ShellLayoutState.InspectorNarrow)]
    [Arguments(0d, ShellLayoutState.Full)]
    [Arguments(double.NaN, ShellLayoutState.Full)]
    public async Task The_breakpoints_close_the_right_pane_first_then_narrow_the_inspector(double width, ShellLayoutState expected)
    {
        await Assert.That(ShellLayoutPolicy.For(width)).IsEqualTo(expected);
    }

    [Test]
    public async Task Each_state_names_its_panes()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ShellLayoutPolicy.ShowsRightPane(ShellLayoutState.Full)).IsTrue();
            await Assert.That(ShellLayoutPolicy.ShowsRightPane(ShellLayoutState.RightPaneClosed)).IsFalse();
            await Assert.That(ShellLayoutPolicy.ShowsRightPane(ShellLayoutState.InspectorNarrow)).IsFalse();
            await Assert.That(ShellLayoutPolicy.InspectorWidthFor(ShellLayoutState.Full)).IsEqualTo(324);
            await Assert.That(ShellLayoutPolicy.InspectorWidthFor(ShellLayoutState.RightPaneClosed)).IsEqualTo(324);
            await Assert.That(ShellLayoutPolicy.InspectorWidthFor(ShellLayoutState.InspectorNarrow)).IsEqualTo(280);        }
    }

    [Test]
    public async Task Linux_draws_its_own_frame_and_every_other_platform_extends_the_os_one()
    {
        await Assert.That(WindowChromePolicy.For(isLinux: true)).IsEqualTo(ChromeMode.Undecorated);
        await Assert.That(WindowChromePolicy.For(isLinux: false)).IsEqualTo(ChromeMode.Extended);
    }

    [Test]
    public async Task The_chrome_reports_its_layout_and_announces_the_panes_it_changes()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        ShellChromeViewModel chrome = new(documents);
        List<string?> changed = [];
        chrome.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        chrome.UpdateWidth(1440);
        await Assert.That(changed).IsEmpty().Because("already full");
        chrome.UpdateWidth(1180);

        using (Assert.Multiple())
        {
            await Assert.That(chrome.Layout).IsEqualTo(ShellLayoutState.RightPaneClosed);
            await Assert.That(chrome.ShowsRightPane).IsFalse();
            await Assert.That(chrome.InspectorWidth).IsEqualTo(324);
            await Assert.That(changed).Contains(nameof(ShellChromeViewModel.Layout));
            await Assert.That(changed).Contains(nameof(ShellChromeViewModel.ShowsRightPane));
            await Assert.That(changed).Contains(nameof(ShellChromeViewModel.InspectorWidth));
        }

        chrome.UpdateWidth(1120);
        await Assert.That(chrome.InspectorWidth).IsEqualTo(280);
    }

    [Test]
    public async Task The_workspace_switch_and_the_requests_reach_the_shell()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        ShellChromeViewModel chrome = new(documents);
        List<ShellRequest> requests = [];
        chrome.Requested += requests.Add;
        bool compareChanged = false;
        chrome.PropertyChanged += (_, e) => compareChanged |= e.PropertyName == nameof(ShellChromeViewModel.IsCompare);

        await Assert.That(chrome.IsCompare).IsFalse();
        await Assert.That(chrome.Workspace.Labels).IsEquivalentTo(new[] { "Inspect", "Compare" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        chrome.Workspace.SelectedIndex = 1;
        chrome.RequestCommand.Execute(ShellRequest.AppMenu);
        chrome.RequestCommand.Execute(ShellRequest.Connect);
        chrome.RequestCommand.Execute(ShellRequest.Open);

        using (Assert.Multiple())
        {
            await Assert.That(chrome.IsCompare).IsTrue();
            await Assert.That(compareChanged).IsTrue();
            await Assert.That(chrome.Workspace.Value).IsEqualTo(Workspace.Compare);
            await Assert.That(requests).IsEquivalentTo(new[] { ShellRequest.AppMenu, ShellRequest.Connect, ShellRequest.Open }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }
}
