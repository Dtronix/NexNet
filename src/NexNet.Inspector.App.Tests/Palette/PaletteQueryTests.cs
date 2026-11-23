using NexNet.Inspector.App.Model.Palette;

namespace NexNet.Inspector.App.Tests.Palette;

/// <summary>The palette's prefixes (OQ14) and its time syntax.</summary>
public sealed class PaletteQueryTests
{
    [Test]
    [Arguments(">trace", PaletteScope.Commands, "trace")]
    [Arguments("# 812", PaletteScope.Frames, "812")]
    [Arguments("@live", PaletteScope.Sessions, "live")]
    [Arguments(":1262.4", PaletteScope.Time, "1262.4")]
    [Arguments("  812 ", PaletteScope.All, "812")]
    [Arguments("", PaletteScope.All, "")]
    public async Task A_leading_prefix_picks_the_scope_and_is_not_searched(string raw, PaletteScope scope, string text)
    {
        PaletteQuery query = PaletteQuery.Parse(raw, PaletteScope.All);

        await Assert.That(query.Scope).IsEqualTo(scope);
        await Assert.That(query.Text).IsEqualTo(text);
        await Assert.That(query.HasPrefix).IsEqualTo(scope != PaletteScope.All);
    }

    [Test]
    public async Task Without_a_prefix_the_pill_decides_and_each_prefix_maps_back_to_its_scope()
    {
        await Assert.That(PaletteQuery.Parse("812", PaletteScope.Entities).Scope).IsEqualTo(PaletteScope.Entities);
        foreach (PaletteScope scope in new[] { PaletteScope.Commands, PaletteScope.Frames, PaletteScope.Sessions, PaletteScope.Time })
            await Assert.That(PaletteQuery.ScopeOf(PaletteQuery.PrefixOf(scope))).IsEqualTo(scope);
        await Assert.That(PaletteQuery.PrefixOf(PaletteScope.Entities)).IsEqualTo('\0');
    }

    [Test]
    [Arguments("1262.4", 1262.4)]
    [Arguments("1,262.4", 1262.4)]
    [Arguments("1262.4 ms", 1262.4)]
    [Arguments("1.2624s", 1262.4)]
    [Arguments("0", 0.0)]
    public async Task Times_read_as_milliseconds_or_seconds(string text, double expected)
    {
        await Assert.That(PaletteQuery.TryParseTime(text, out double ms)).IsTrue();
        await Assert.That(Math.Abs(ms - expected)).IsLessThan(1e-9);
    }

    [Test]
    [Arguments("")]
    [Arguments("abc")]
    [Arguments("ms")]
    [Arguments("12:30")]
    public async Task Anything_else_is_not_a_time(string text)
    {
        await Assert.That(PaletteQuery.TryParseTime(text, out _)).IsFalse();
    }
}
