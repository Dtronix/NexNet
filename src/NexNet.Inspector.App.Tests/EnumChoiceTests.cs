using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Tests;

/// <summary>The enum-backed choice a segmented control binds: index and value stay one, and nothing else gets in.</summary>
public sealed class EnumChoiceTests
{
    private static EnumChoice<InspectorThemeProbe> Theme(InspectorThemeProbe selected = InspectorThemeProbe.System) =>
        new([(InspectorThemeProbe.Dark, "Dark"), (InspectorThemeProbe.Light, "Light"), (InspectorThemeProbe.System, "System")], selected);

    [Test]
    public async Task Labels_and_values_keep_the_given_order()
    {
        EnumChoice<InspectorThemeProbe> choice = Theme();
        using (Assert.Multiple())
        {
            await Assert.That(choice.Labels).IsEquivalentTo(new[] { "Dark", "Light", "System" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(choice.Values).IsEquivalentTo(new[] { InspectorThemeProbe.Dark, InspectorThemeProbe.Light, InspectorThemeProbe.System }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(choice.SelectedIndex).IsEqualTo(2);
            await Assert.That(choice.Value).IsEqualTo(InspectorThemeProbe.System);
        }
    }

    [Test]
    public async Task Moving_the_index_moves_the_value_and_notifies_once()
    {
        EnumChoice<InspectorThemeProbe> choice = Theme();
        List<string> changed = [];
        List<InspectorThemeProbe> values = [];
        choice.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        choice.ValueChanged += (_, v) => values.Add(v);

        choice.SelectedIndex = 1;
        choice.SelectedIndex = 1;

        using (Assert.Multiple())
        {
            await Assert.That(choice.Value).IsEqualTo(InspectorThemeProbe.Light);
            await Assert.That(changed).IsEquivalentTo(new[] { nameof(EnumChoice<InspectorThemeProbe>.SelectedIndex), nameof(EnumChoice<InspectorThemeProbe>.Value) }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(values).IsEquivalentTo(new[] { InspectorThemeProbe.Light }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task An_index_outside_the_options_is_ignored()
    {
        EnumChoice<InspectorThemeProbe> choice = Theme(InspectorThemeProbe.Dark);
        choice.SelectedIndex = -1;
        choice.SelectedIndex = 3;
        await Assert.That(choice.Value).IsEqualTo(InspectorThemeProbe.Dark);
    }

    [Test]
    public async Task Setting_the_value_selects_its_option()
    {
        EnumChoice<InspectorThemeProbe> choice = Theme();
        choice.Value = InspectorThemeProbe.Dark;
        await Assert.That(choice.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Options_are_checked()
    {
        using (Assert.Multiple())
        {
            await Assert.That(() => new EnumChoice<InspectorThemeProbe>([], InspectorThemeProbe.Dark)).Throws<ArgumentException>();
            await Assert.That(() => new EnumChoice<InspectorThemeProbe>([(InspectorThemeProbe.Dark, "a"), (InspectorThemeProbe.Dark, "b")], InspectorThemeProbe.Dark)).Throws<ArgumentException>();
            await Assert.That(() => new EnumChoice<InspectorThemeProbe>([(InspectorThemeProbe.Dark, "a")], InspectorThemeProbe.Light)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => Theme().Value = (InspectorThemeProbe)9).Throws<ArgumentOutOfRangeException>();
        }
    }
}
