using System;
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
    public void GeneratorParity_EditorServerInterface_AssignsExpectedIds()
    {
        var map = MethodIdMap.Build(typeof(IEditorServerNexus));

        // Source declaration order in EditorAppNexus.cs.
        AssertMethodId(map, nameof(IEditorServerNexus.Ping), 0);
        AssertMethodId(map, nameof(IEditorServerNexus.Notify), 1);
        AssertMethodId(map, nameof(IEditorServerNexus.Upload), 2);
        AssertMethodId(map, nameof(IEditorServerNexus.Download), 3);
        AssertMethodId(map, nameof(IEditorServerNexus.CollectStrings), 4);
        AssertMethodId(map, nameof(IEditorServerNexus.PublishStrings), 5);
        AssertMethodId(map, nameof(IEditorServerNexus.OpenDocument), 6);
        AssertMethodId(map, nameof(IEditorServerNexus.LeaveDocument), 7);
        AssertMethodId(map, nameof(IEditorServerNexus.SaveDraft), 8);
        AssertMethodId(map, nameof(IEditorServerNexus.Whisper), 9);
        AssertMethodId(map, nameof(IEditorServerNexus.BroadcastSystemAnnouncement), 10);
        AssertMethodId(map, nameof(IEditorServerNexus.ListActiveEditors), 11);
        AssertMethodId(map, nameof(IEditorServerNexus.UploadAttachment), 12);
        AssertMethodId(map, nameof(IEditorServerNexus.StreamEdits), 13);
    }

    [Test]
    public void GeneratorParity_EditorClientInterface_AssignsExpectedIds()
    {
        var map = MethodIdMap.Build(typeof(IEditorClientNexus));
        AssertMethodId(map, nameof(IEditorClientNexus.DraftSaved), 0);
        AssertMethodId(map, nameof(IEditorClientNexus.EditorJoined), 1);
        AssertMethodId(map, nameof(IEditorClientNexus.EditorLeft), 2);
        AssertMethodId(map, nameof(IEditorClientNexus.WhisperReceived), 3);
        AssertMethodId(map, nameof(IEditorClientNexus.SystemAnnouncement), 4);
    }

    [Test]
    public void Build_IsDeterministic_AcrossInvocations()
    {
        var first = MethodIdMap.Build(typeof(IEditorServerNexus));
        var second = MethodIdMap.Build(typeof(IEditorServerNexus));
        var third = MethodIdMap.Build(typeof(IEditorServerNexus));

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

    [Test]
    public void Build_ExcludesIgnoredMethods_AndDoesNotShiftFollowingIds()
    {
        // The generator filters [NexusMethod(Ignore = true)] methods *before* assigning ids, so
        // an ignored method receives no id and does not reserve a slot. The runtime map must match:
        // First=0, Third=1, and Ignored absent entirely. (Pre-fix, the map would have included
        // Ignored at id 1 and pushed Third to 2 — a silent off-by-one against the generator.)
        var map = MethodIdMap.Build(typeof(IIgnoreSample));

        AssertMethodId(map, nameof(IIgnoreSample.First), 0);
        AssertMethodId(map, nameof(IIgnoreSample.Third), 1);
        Assert.That(map.Keys.Any(m => m.Name == nameof(IIgnoreSample.Ignored)), Is.False,
            "Ignored methods must be excluded from the map entirely (they get no generator id).");
    }

    [Test]
    public void Build_OrdersGenericInheritedInterfaces_ByDisplayName_NotFullName()
    {
        // The generator orders inherited interfaces by INamedTypeSymbol.ToDisplayString() (ordinal),
        // e.g. "...IGen<System.Guid>" / "...IGen<bool>". Type.FullName uses the CLR generic encoding
        // ("...IGen`1[[System.Boolean, ...]]") whose type-arg ordering diverges here: by display
        // name IGen<Guid> sorts before IGen<bool> ('S' < 'b'), but by FullName IGen<bool> sorts
        // first ('Boolean' < 'Guid'). The map must follow the generator (display-name) order.
        var map = MethodIdMap.Build(typeof(IGenericDerived));

        AssertMethodId(map, nameof(IGenericDerived.Direct), 0);
        // Direct method first (id 0); then inherited-interface methods in display-name order.
        AssertGenericMethodId(map, typeof(Guid), 1);   // IGen<Guid>.GenMethod
        AssertGenericMethodId(map, typeof(bool), 2);   // IGen<bool>.GenMethod
    }

    private static void AssertMethodId(System.Collections.Generic.Dictionary<MethodInfo, ushort> map, string methodName, ushort expectedId)
    {
        var method = map.Keys.FirstOrDefault(m => m.Name == methodName);
        Assert.That(method, Is.Not.Null, $"Method {methodName} missing from map");
        Assert.That(map[method!], Is.EqualTo(expectedId), $"Method {methodName} mapped to wrong id");
    }

    // Disambiguates the two IGen<T>.GenMethod overloads by their single parameter type.
    private static void AssertGenericMethodId(System.Collections.Generic.Dictionary<MethodInfo, ushort> map, System.Type paramType, ushort expectedId)
    {
        var method = map.Keys.FirstOrDefault(m =>
            m.Name == nameof(IGen<object>.GenMethod) &&
            m.GetParameters() is { Length: 1 } p && p[0].ParameterType == paramType);
        Assert.That(method, Is.Not.Null, $"GenMethod({paramType.Name}) missing from map");
        Assert.That(map[method!], Is.EqualTo(expectedId), $"GenMethod({paramType.Name}) mapped to wrong id");
    }

    private interface IExplicitIdSample
    {
        void FirstMethod();
        [NexNet.NexusMethod(MethodId = 5)]
        void PinnedToFive();
        void SecondMethod();
    }

    private interface IIgnoreSample
    {
        void First();
        [NexNet.NexusMethod(Ignore = true)]
        void Ignored();
        void Third();
    }

    private interface IGen<T>
    {
        void GenMethod(T value);
    }

    private interface IGenericDerived : IGen<Guid>, IGen<bool>
    {
        void Direct();
    }
}
