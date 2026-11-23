using Avalonia.Data.Converters;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// Text shaping the menu theme needs and Avalonia's TextBlock lacks (it has no CSS text-transform): a group heading is
/// written in sentence case by its caller and drawn in capitals, so screen readers still read words, not letters.
/// </summary>
public static class MenuText
{
    /// <summary>A string header in invariant upper case; anything else as its text.</summary>
    public static IValueConverter Upper { get; } = new FuncValueConverter<object?, string?>(static v => v is string s ? s.ToUpperInvariant() : v?.ToString());
}
