using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>A stream position: a direction and an offset in it.</summary>
/// <param name="Direction">The stream.</param>
/// <param name="Offset">The byte's absolute stream offset.</param>
public readonly record struct HexPosition(TapDirection Direction, long Offset);

/// <summary>
/// The hex menu's "Go to offset…" dialog: the offset in hex, as the hex view's gutter writes it (a 0x prefix is
/// allowed), and the stream it is in (▼ IN | ▲ OUT). Enter goes there when the offset is held in that stream; otherwise
/// the field says which range is. Esc cancels. <see cref="Result"/> completes with the position, or null on cancel.
/// </summary>
public sealed partial class GoToOffsetDialogViewModel : ObservableObject
{
    private readonly Func<TapDirection, (long Start, long End)> _bounds;
    private readonly TaskCompletionSource<HexPosition?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="position">Where the dialog starts: the selection's direction and first byte.</param>
    /// <param name="bounds">The offsets each stream holds, [Start, End).</param>
    public GoToOffsetDialogViewModel(HexPosition position, Func<TapDirection, (long Start, long End)> bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        _bounds = bounds;
        Direction = new EnumChoice<TapDirection>([(TapDirection.Inbound, "▼ IN"), (TapDirection.Outbound, "▲ OUT")], position.Direction);
        Direction.ValueChanged += (_, _) => Error = null;
        Text = ByteText.Offset(position.Offset);
    }

    /// <summary>Raised once the dialog is done (gone to, or canceled): the host removes it.</summary>
    public event Action<GoToOffsetDialogViewModel>? Closed;

    public string Title => "Go to offset";

    public string Hint => "Enter to go · Esc to cancel";

    /// <summary>The stream: ▼ IN | ▲ OUT.</summary>
    public EnumChoice<TapDirection> Direction { get; }

    /// <summary>The offset typed, in hex.</summary>
    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>Why the offset cannot be gone to; null when it can (or has not been tried).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    /// <summary>True while the field shows its error line.</summary>
    public bool HasError => Error is not null;

    /// <summary>The position chosen; null when canceled.</summary>
    public Task<HexPosition?> Result => _result.Task;

    /// <summary>Parses an offset as the gutter writes it: hex digits, optionally after 0x.</summary>
    public static bool TryParse(string? text, out long offset)
    {
        offset = 0;
        ReadOnlySpan<char> s = (text ?? "").AsSpan().Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return !s.IsEmpty && long.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out offset) && offset >= 0;
    }

    partial void OnTextChanged(string value) => Error = null;

    /// <summary>Enter and the primary button: go, when the offset is in the stream.</summary>
    [RelayCommand]
    private void Submit()
    {
        if (_result.Task.IsCompleted) return;
        if (!TryParse(Text, out long offset))
        {
            Error = "Type a hex offset, as the gutter shows it (1A3F21)";
            return;
        }

        TapDirection direction = Direction.Value;
        (long start, long end) = _bounds(direction);
        if (offset < start || offset >= end)
        {
            Error = end <= start
                ? "No " + (direction == TapDirection.Inbound ? "inbound" : "outbound") + " bytes are held"
                : "Outside the stream: " + ByteText.Range(start, end - start);
            return;
        }

        Finish(new HexPosition(direction, offset));
    }

    /// <summary>Esc and Cancel.</summary>
    [RelayCommand]
    private void Cancel() => Finish(null);

    private void Finish(HexPosition? position)
    {
        if (!_result.TrySetResult(position)) return;
        Closed?.Invoke(this);
    }
}
