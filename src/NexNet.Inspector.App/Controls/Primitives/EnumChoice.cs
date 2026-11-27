using CommunityToolkit.Mvvm.ComponentModel;

namespace NexNet.Inspector.App.Controls.Primitives;

/// <summary>
/// An enum-backed choice for a segmented control (or any single-choice list): the options in display order with their
/// labels, and the current value. Bind a Segmented ListBox's ItemsSource to <see cref="Labels"/> and its SelectedIndex
/// to <see cref="SelectedIndex"/>; read and write <see cref="Value"/> in the view-model. The view never sees the enum
/// and the view-model never compares labels: the index is the only link between them.
/// </summary>
public sealed class EnumChoice<TEnum> : ObservableObject
    where TEnum : struct, Enum
{
    private readonly TEnum[] _values;
    private readonly string[] _labels;
    private int _selectedIndex;

    /// <param name="options">The options in display order, each with its label.</param>
    /// <param name="selected">The initial value; it must be one of the options.</param>
    public EnumChoice(ReadOnlySpan<(TEnum Value, string Label)> options, TEnum selected)
    {
        if (options.IsEmpty) throw new ArgumentException("a choice needs at least one option", nameof(options));
        _values = new TEnum[options.Length];
        _labels = new string[options.Length];
        for (int i = 0; i < options.Length; i++)
        {
            if (Array.IndexOf(_values, options[i].Value, 0, i) >= 0) throw new ArgumentException($"{options[i].Value} is listed twice", nameof(options));
            _values[i] = options[i].Value;
            _labels[i] = options[i].Label;
        }

        _selectedIndex = IndexOf(selected);
        if (_selectedIndex < 0) throw new ArgumentOutOfRangeException(nameof(selected), selected, "not one of the options");
    }

    /// <summary>The options' labels, in display order.</summary>
    public IReadOnlyList<string> Labels => _labels;

    /// <summary>The options' values, in display order.</summary>
    public IReadOnlyList<TEnum> Values => _values;

    /// <summary>
    /// The selected option's position. A value outside the options (a list briefly reporting -1 while its items are
    /// replaced) is ignored, so the choice always holds one of its values.
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if ((uint)value >= (uint)_values.Length || value == _selectedIndex) return;
            _selectedIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Value));
            ValueChanged?.Invoke(this, _values[value]);
        }
    }

    /// <summary>The selected value. Setting a value that is not an option throws.</summary>
    public TEnum Value
    {
        get => _values[_selectedIndex];
        set
        {
            int index = IndexOf(value);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "not one of the options");
            SelectedIndex = index;
        }
    }

    /// <summary>Raised after the selection moves, with the new value.</summary>
    public event EventHandler<TEnum>? ValueChanged;

    /// <summary>The position of <paramref name="value"/>, or -1 when it is not an option.</summary>
    public int IndexOf(TEnum value) => Array.IndexOf(_values, value);
}
