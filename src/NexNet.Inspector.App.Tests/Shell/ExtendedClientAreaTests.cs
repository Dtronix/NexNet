using Avalonia.Controls;
using NexNet.Inspector.App.Platform;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// Whether the main window still has its extended client area, read from the platform window alone (the property
/// <c>IsExtendedIntoWindowDecorations</c> stays true after Avalonia 12.1.3 drops it). Normal, BorderOnly + extended
/// leaves the client inset by the frame at the sides and bottom and by 1 px at the top; lost, the inset is the frame on
/// all four sides. Maximized, both look alike in rects, so the window's own WM_NCHITTEST answer in its top strip decides.
/// </summary>
public sealed class ExtendedClientAreaTests
{
    private const int HtNowhere = 0;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtTop = 12;

    private static readonly ScreenRect NoWorkArea = new(0, 0, 0, 0);
    private static readonly ScreenRect WorkArea = new(0, 0, 1712, 912);
    private static readonly ScreenRect MaximizedWindow = new(-8, -8, 1720, 920);

    [Test]
    public async Task ClientTheWindowsSizeIsExtended()
    {
        ScreenRect window = new(200, 100, 1700, 1020);
        await Assert.That(ExtendedClientArea.Classify(window, window, WindowState.Normal, NoWorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Extended);
    }

    [Test]
    public async Task TheHealthyInsetsOfAvalonia1213AreExtended()
    {
        ScreenRect window = new(200, 100, 1700, 1020);
        ScreenRect client = new(208, 101, 1692, 1012);
        await Assert.That(ExtendedClientArea.Classify(window, client, WindowState.Normal, NoWorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Extended);
    }

    [Test]
    public async Task TheHealthyInsetsAt150PercentAreExtended()
    {
        ScreenRect window = new(200, 100, 1700, 1020);
        ScreenRect client = new(211, 101, 1689, 1009);
        await Assert.That(ExtendedClientArea.Classify(window, client, WindowState.Normal, NoWorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Extended);
    }

    [Test]
    public async Task AClientInsetBy8PxOnEverySideIsLost()
    {
        // The operator's probe: 1712x912 inside 1728x928.
        ScreenRect window = new(200, 100, 1928, 1028);
        ScreenRect client = new(208, 108, 1920, 1020);
        await Assert.That(ExtendedClientArea.Classify(window, client, WindowState.Normal, NoWorkArea, HtCaption)).IsEqualTo(ExtendedClientState.Lost);
    }

    [Test]
    public async Task AClientInsetByTheFrameAt150PercentIsLost()
    {
        ScreenRect window = new(200, 100, 1700, 1020);
        ScreenRect client = new(211, 111, 1689, 1009);
        await Assert.That(ExtendedClientArea.Classify(window, client, WindowState.Normal, NoWorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Lost);
    }

    [Test]
    public async Task MaximizedOverhangingTheMonitorAndAnsweringCaptionIsExtended()
    {
        await Assert.That(ExtendedClientArea.Classify(MaximizedWindow, WorkArea, WindowState.Maximized, WorkArea, HtCaption)).IsEqualTo(ExtendedClientState.Extended);
    }

    [Test]
    [Arguments(HtClient)]
    [Arguments(HtTop)]
    public async Task MaximizedAnsweringAnythingButCaptionIsLost(int hit)
    {
        using (Assert.Multiple())
        {
            await Assert.That(ExtendedClientArea.Classify(MaximizedWindow, WorkArea, WindowState.Maximized, WorkArea, hit)).IsEqualTo(ExtendedClientState.Lost).Because("overhanging");
            await Assert.That(ExtendedClientArea.Classify(WorkArea, new ScreenRect(8, 8, 1704, 904), WindowState.Maximized, WorkArea, hit)).IsEqualTo(ExtendedClientState.Lost).Because("filling the monitor");
        }
    }

    [Test]
    public async Task MaximizedWithNoHitTestAnswerIsUnknown()
    {
        await Assert.That(ExtendedClientArea.Classify(MaximizedWindow, WorkArea, WindowState.Maximized, WorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Unknown);
    }

    [Test]
    public async Task MaximizedWithoutCoveringItsWorkAreaIsUnknown()
    {
        // Mid-move across monitors: the work area read is not the one the window fills.
        ScreenRect window = new(1900, -8, 3628, 920);
        ScreenRect client = new(1908, 0, 3620, 912);
        await Assert.That(ExtendedClientArea.Classify(window, client, WindowState.Maximized, WorkArea, HtClient)).IsEqualTo(ExtendedClientState.Unknown);
    }

    [Test]
    public async Task ZeroNegativeOrDegenerateInputIsUnknown()
    {
        ScreenRect window = new(200, 100, 1700, 1020);
        ScreenRect lostClient = new(208, 108, 1692, 1012);
        using (Assert.Multiple())
        {
            await Assert.That(ExtendedClientArea.Classify(default, default, WindowState.Normal, NoWorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Unknown).Because("zero rects");
            await Assert.That(ExtendedClientArea.Classify(window, new ScreenRect(208, 108, 208, 108), WindowState.Normal, NoWorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Unknown).Because("an empty client");
            await Assert.That(ExtendedClientArea.Classify(new ScreenRect(1700, 1020, 200, 100), lostClient, WindowState.Normal, NoWorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Unknown).Because("a negative window");
            await Assert.That(ExtendedClientArea.Classify(window, new ScreenRect(100, 50, 1800, 1100), WindowState.Normal, NoWorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Unknown).Because("a client larger than its window");
            await Assert.That(ExtendedClientArea.Classify(window, new ScreenRect(203, 104, 1690, 1018), WindowState.Normal, NoWorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Unknown).Because("uneven insets");
            await Assert.That(ExtendedClientArea.Classify(window, lostClient, WindowState.Minimized, NoWorkArea, HtNowhere)).IsEqualTo(ExtendedClientState.Unknown).Because("minimized");
            await Assert.That(ExtendedClientArea.Classify(MaximizedWindow, WorkArea, WindowState.Maximized, NoWorkArea, HtClient)).IsEqualTo(ExtendedClientState.Unknown).Because("maximized with no work area");
        }
    }
}
