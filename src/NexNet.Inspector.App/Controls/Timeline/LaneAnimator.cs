using System.Diagnostics;
using Avalonia.Animation.Easings;
using NexNet.Inspector.App.Platform;

namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>
/// Lane heights over time (OQ12): a lane that appears, disappears or expands moves to its new height over 160 ms with
/// the standard ease, cubic-bezier(.2,0,0,1), and no fade; when the OS reduces motion it jumps at once. Keyed by lane,
/// so a lane keeps its motion across model refreshes. Times are Stopwatch ticks.
/// </summary>
public sealed class LaneAnimator
{
    /// <summary>The height motion's length.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(160);

    private readonly Dictionary<LaneKey, Motion> _motions = [];
    private readonly long _duration;
    private long _latestEnd;

    /// <param name="enabled">False applies every change at once; by default the OS's motion preference decides.</param>
    /// <param name="ease">The ease; the design's standard ease by default.</param>
    public LaneAnimator(bool? enabled = null, Easing? ease = null)
    {
        Enabled = enabled ?? SystemMotion.ShowsAnimations();
        Ease = ease ?? ScrollRail.Ease;
        _duration = (long)(Duration.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>False when height changes apply at once (the OS reduces motion).</summary>
    public bool Enabled { get; set; }

    /// <summary>The ease applied to the motion's progress.</summary>
    public Easing Ease { get; }

    /// <summary>
    /// The lane's height at <paramref name="now"/> on its way to <paramref name="target"/>. A new target starts a
    /// motion from wherever the lane is; a lane seen for the first time starts at its target when
    /// <paramref name="instant"/>, otherwise from 0 (it is being added).
    /// </summary>
    public double Height(LaneKey key, double target, long now, bool instant = false)
    {
        ref Motion motion = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_motions, key, out bool existed);
        if (!existed)
        {
            motion = new Motion(instant || !Enabled ? target : 0, target, now);
        }
        else if (motion.To != target)
        {
            double from = instant || !Enabled ? target : Current(motion, now);
            motion = new Motion(from, target, now);
        }

        if (motion.From != motion.To && now - motion.Start < _duration)
            _latestEnd = Math.Max(_latestEnd, motion.Start + _duration);
        return Current(motion, now);
    }

    /// <summary>True while a motion started by <see cref="Height"/> has not finished at <paramref name="now"/>.</summary>
    public bool IsAnimating(long now) => now < _latestEnd;

    /// <summary>Forgets every lane (a new document appears without motion).</summary>
    public void Reset()
    {
        _motions.Clear();
        _latestEnd = 0;
    }

    private double Current(in Motion motion, long now)
    {
        if (motion.From == motion.To) return motion.To;
        double t = (double)(now - motion.Start) / _duration;
        if (t >= 1) return motion.To;
        if (t <= 0) return motion.From;
        return motion.From + ((motion.To - motion.From) * Ease.Ease(t));
    }

    private readonly record struct Motion(double From, double To, long Start);
}
