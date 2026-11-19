namespace NexNet.Generator.Tests.Runtime;

/// <summary>The C# nexus pair of the emitted-runtime proof (D31): one declared type, a server and a client method.</summary>
internal static class EmittedRuntimeFixture
{
    public const string Source = """
        #nullable enable
        using System;
        using System.Threading.Tasks;
        using NexNet;
        using NexNet.Serialization;

        namespace Proof;

        [NexusObject]
        public class Note
        {
            [NexusKey(0)] public int Id { get; set; }
            [NexusKey(1)] public string? Text { get; set; }
        }

        public interface IProofClient { ValueTask Tell(Note note); }
        public interface IProofServer { ValueTask<Note?> Echo(Note note, string? tag); ValueTask<int> Add(int a, int b); }

        [Nexus<IProofClient, IProofServer>(NexusType = NexusType.Client)]
        public partial class ProofClientNexus { public ValueTask Tell(Note note) => default; }

        [Nexus<IProofServer, IProofClient>(NexusType = NexusType.Server, Targets = NexusTargets.Java | NexusTargets.JavaScript)]
        public partial class ProofServerNexus
        {
            public ValueTask<Note?> Echo(Note note, string? tag) => new(note);
            public ValueTask<int> Add(int a, int b) => new(a + b);
        }
        """;
}
