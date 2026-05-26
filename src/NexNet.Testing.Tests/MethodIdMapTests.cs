using System.Linq;
using System.Reflection;
using NexNet.Testing.Recording;
using NUnit.Framework;

namespace NexNet.Testing.Tests;

/// <summary>
/// Regression coverage for <see cref="MethodIdMap.Build"/>. The map is the load-bearing piece
/// of the assertion API — if its output drifts from the generator-burned ids, asserts silently
/// match the wrong method (or none at all). These tests pin the expected ids for the demo
/// interfaces against the known source-declaration order.
/// </summary>
internal class MethodIdMapTests
{
    [Test]
    public void GeneratorParity_DemoServerInterface_AssignsExpectedIds()
    {
        var map = MethodIdMap.Build(typeof(IDemoServerNexus));

        // Source declaration order in HarnessSampleNexus.cs:
        //   Ping, Notify, JoinGroup, BroadcastToGroup, Upload, Download,
        //   CollectStrings, PublishStrings.
        AssertMethodId(map, nameof(IDemoServerNexus.Ping), 0);
        AssertMethodId(map, nameof(IDemoServerNexus.Notify), 1);
        AssertMethodId(map, nameof(IDemoServerNexus.JoinGroup), 2);
        AssertMethodId(map, nameof(IDemoServerNexus.BroadcastToGroup), 3);
        AssertMethodId(map, nameof(IDemoServerNexus.Upload), 4);
        AssertMethodId(map, nameof(IDemoServerNexus.Download), 5);
        AssertMethodId(map, nameof(IDemoServerNexus.CollectStrings), 6);
        AssertMethodId(map, nameof(IDemoServerNexus.PublishStrings), 7);
    }

    [Test]
    public void GeneratorParity_DemoClientInterface_AssignsExpectedIds()
    {
        var map = MethodIdMap.Build(typeof(IDemoClientNexus));
        AssertMethodId(map, nameof(IDemoClientNexus.ReceiveBroadcast), 0);
    }

    [Test]
    public void Build_IsDeterministic_AcrossInvocations()
    {
        var first = MethodIdMap.Build(typeof(IDemoServerNexus));
        var second = MethodIdMap.Build(typeof(IDemoServerNexus));
        var third = MethodIdMap.Build(typeof(IDemoServerNexus));

        foreach (var (method, id) in first)
        {
            Assert.That(second[method], Is.EqualTo(id),
                $"Build order changed for {method.Name} on second call");
            Assert.That(third[method], Is.EqualTo(id),
                $"Build order changed for {method.Name} on third call");
        }
    }

    [Test]
    public void Build_ExplicitMethodId_TakesPrecedenceAndSkipsSlot()
    {
        // Synthetic interface with one explicit id to validate the precedence + slot-skipping
        // logic without depending on the demo interfaces.
        var map = MethodIdMap.Build(typeof(IExplicitIdSample));

        AssertMethodId(map, nameof(IExplicitIdSample.FirstMethod), 0);
        AssertMethodId(map, nameof(IExplicitIdSample.PinnedToFive), 5);
        // SecondMethod is third in source order; gets next available id (skipping 5).
        AssertMethodId(map, nameof(IExplicitIdSample.SecondMethod), 1);
    }

    private static void AssertMethodId(System.Collections.Generic.Dictionary<MethodInfo, ushort> map, string methodName, ushort expectedId)
    {
        var method = map.Keys.FirstOrDefault(m => m.Name == methodName);
        Assert.That(method, Is.Not.Null, $"Method {methodName} missing from map");
        Assert.That(map[method!], Is.EqualTo(expectedId), $"Method {methodName} mapped to wrong id");
    }

    private interface IExplicitIdSample
    {
        void FirstMethod();
        [NexNet.NexusMethod(MethodId = 5)]
        void PinnedToFive();
        void SecondMethod();
    }
}
