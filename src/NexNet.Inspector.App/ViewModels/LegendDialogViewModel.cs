using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>One section of the legend: its heading and its lines.</summary>
/// <param name="Title">The heading.</param>
/// <param name="Entries">Its lines, in reading order.</param>
public sealed record LegendGroup(string Title, IReadOnlyList<LegendEntry> Entries);

/// <summary>
/// Legend &amp; symbols (F2, app menu › View): every symbol the views draw, from <see cref="Legend"/>, by section in two
/// balanced columns. Enter and Esc close it.
/// </summary>
public sealed partial class LegendDialogViewModel
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LegendDialogViewModel() => (Left, Right) = Columns();

    /// <summary>Raised once closed: the host removes the dialog.</summary>
    public event Action<LegendDialogViewModel>? Closed;

    public string Title => "Legend & symbols";

    public string Hint => "Esc to close · hover a chip in a view for its tooltip";

    /// <summary>The first column's sections.</summary>
    public IReadOnlyList<LegendGroup> Left { get; }

    /// <summary>The second column's sections.</summary>
    public IReadOnlyList<LegendGroup> Right { get; }

    /// <summary>Completes when the dialog closes.</summary>
    public Task Result => _closed.Task;

    /// <summary>The sections in reading order, split where the first column holds about half of the lines (headings count as one).</summary>
    public static (LegendGroup[] Left, LegendGroup[] Right) Columns()
    {
        List<LegendGroup> groups = [];
        int total = 0;
        for (int s = 0; s < Legend.SectionCount; s++)
        {
            LegendSection section = (LegendSection)s;
            LegendEntry[] entries = Legend.Entries.Where(e => e.Section == section).ToArray();
            if (entries.Length == 0) continue;
            groups.Add(new LegendGroup(Legend.Title(section), entries));
            total += entries.Length + 1;
        }

        int split = 0, taken = 0;
        while (split < groups.Count && taken + ((groups[split].Entries.Count + 1) / 2) < total / 2)
        {
            taken += groups[split].Entries.Count + 1;
            split++;
        }

        return ([.. groups.Take(split)], [.. groups.Skip(split)]);
    }

    /// <summary>Enter, Esc and Close.</summary>
    [RelayCommand]
    private void Close()
    {
        if (_closed.Task.IsCompleted) return;
        _closed.TrySetResult();
        Closed?.Invoke(this);
    }
}
