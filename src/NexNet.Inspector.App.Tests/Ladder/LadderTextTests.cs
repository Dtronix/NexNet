using NexNet.Inspector.App.Controls.Ladder;
using static NexNet.Inspector.App.Tests.Ladder.LadderDataBuilder;

namespace NexNet.Inspector.App.Tests.Ladder;

/// <summary>A row's words, as the package's inspect-live Ladder pane writes them and a screen reader announces them.</summary>
public sealed class LadderTextTests
{
    [Test]
    public async Task Rows_read_as_the_package_writes_them()
    {
        LadderModel model = Demo();

        await Assert.That(LadderText.Title(model[6], model.ChunkTotal(model[6].Message))).IsEqualTo("MessageChunk 2/5");
        await Assert.That(LadderText.Detail(model[6], null)).IsEqualTo("inv 812");
        await Assert.That(LadderText.Bytes(model[6])).IsEqualTo("4,096 B");
        await Assert.That(LadderText.Time(model[6].Ticks, model.Origin, model.Frequency)).IsEqualTo("1,262.418");
        await Assert.That(LadderText.Detail(model[0], null)).IsEqualTo("inv 812 · Arg");
        await Assert.That(LadderText.Detail(model[14], null)).IsEqualTo("inv 812 · Completed");
        await Assert.That(LadderText.Detail(model[5], null)).IsEqualTo("pipe 3·1");
        await Assert.That(LadderText.Detail(model[12], model.GetLabel(model[12].Name))).IsEqualTo("chan 4·2 LogLine");
        await Assert.That(LadderText.Detail(model[15], model.GetLabel(model[15].Name))).IsEqualTo("inv 813 GetHistory");
        await Assert.That(LadderText.Detail(model[20], null)).IsEqualTo("pipe 3·1 · partial");
        await Assert.That(LadderText.Title(model[4], 0)).IsEqualTo("Ping");
        await Assert.That(LadderText.Bytes(model[4])).IsNull();
        await Assert.That(LadderText.Detail(model[4], null)).IsEqualTo("");
    }

    [Test]
    public async Task A_row_is_announced_with_its_time_direction_type_target_bytes_and_flags()
    {
        LadderModel model = Demo();
        string text = LadderText.Describe(model[6], 5, null, model.Origin, model.Frequency);

        await Assert.That(text).IsEqualTo("1,262.418 ms inbound MessageChunk 2/5 invocation 812, 4,096 bytes, transport split, NexNet split");
    }
}
