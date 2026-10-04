using System.Runtime.CompilerServices;

namespace NexNet.Serialization;

internal static class NexusSerializationModule
{
#pragma warning disable CA2255 // Library initialization of the built-in formatter registry.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        NexusFormatterRegistry.EnsureInitialized();
    }
}
