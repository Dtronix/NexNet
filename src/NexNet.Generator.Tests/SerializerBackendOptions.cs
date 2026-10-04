using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NexNet.Generator.Tests;

/// <summary>
/// Analyzer config options selecting the payload serializer backend (build_property.NexNetSerializer).
/// </summary>
internal sealed class SerializerBackendOptions : AnalyzerConfigOptionsProvider
{
    public static readonly SerializerBackendOptions MemoryPack = new("MemoryPack");
    public static readonly SerializerBackendOptions MessagePack = new("MessagePack");

    private readonly BackendGlobalOptions _global;

    private SerializerBackendOptions(string backend)
    {
        _global = new BackendGlobalOptions(backend);
    }

    public override AnalyzerConfigOptions GlobalOptions => _global;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _global;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _global;

    private sealed class BackendGlobalOptions(string backend) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
        {
            if (key == "build_property.NexNetSerializer")
            {
                value = backend;
                return true;
            }

            value = null;
            return false;
        }
    }
}
