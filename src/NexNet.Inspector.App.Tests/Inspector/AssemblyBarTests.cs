using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Tests.Capture;

namespace NexNet.Inspector.App.Tests.Inspector;

/// <summary>The assembly bar's layout: S fixed, chunks by bytes, gaps widened where frames were interleaved.</summary>
public class AssemblyBarTests
{
    [Test]
    public async Task Segments_share_the_width_by_bytes_and_fit_it()
    {
        AssemblySegment[] segments = InspectGallery.SampleSegments();

        (double X, double Width)[] boxes = AssemblyBar.Layout(segments, 300);

        await Assert.That(boxes[0].Width).IsEqualTo(AssemblyBar.MinSegmentWidth);
        await Assert.That(Math.Abs(boxes[1].Width - boxes[2].Width)).IsLessThan(0.001);
        await Assert.That(boxes[5].Width).IsLessThan(boxes[4].Width);
        await Assert.That(Math.Abs((boxes[5].Width / boxes[4].Width) - (2038.0 / 4096))).IsLessThan(0.01);
        await Assert.That(boxes[^1].X + boxes[^1].Width).IsLessThanOrEqualTo(300.001);
        // Chunk 2 followed two interleaved frames: its gap is the wide one.
        await Assert.That(boxes[2].X - (boxes[1].X + boxes[1].Width)).IsEqualTo(AssemblyBar.InterleavedGap);
        await Assert.That(boxes[1].X - (boxes[0].X + boxes[0].Width)).IsEqualTo(AssemblyBar.Gap);
        await Assert.That(segments[0].Label).IsEqualTo("S");
        await Assert.That(segments[3].Label).IsEqualTo("3");
    }

    [Test]
    public async Task A_bar_too_narrow_for_its_floors_scales_to_fit()
    {
        AssemblySegment[] segments = [.. Enumerable.Range(1, 40).Select(i => new AssemblySegment(AssemblySegmentKind.Chunk, i, 100, false, 0))];

        (double X, double Width)[] boxes = AssemblyBar.Layout(segments, 200);

        await Assert.That(boxes[^1].X + boxes[^1].Width).IsLessThanOrEqualTo(200.001);
    }
}
