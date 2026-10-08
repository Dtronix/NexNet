using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NexNet.Fuzz;

/// <summary>
/// SharpFuzz-instrumented code writes coverage into <c>SharpFuzz.Common.Trace.SharedMem</c>, which stays null until
/// <c>Fuzzer.LibFuzzer.Run</c> starts. NexNet code already runs before that, from the generated formatter
/// registration module initializers, so point the trace at a scratch buffer first. Hand-written module initializers
/// are emitted before generated ones, so this runs first. The fuzzer replaces the buffer with its shared memory.
/// </summary>
internal static unsafe class InstrumentationBootstrap
{
    private const int MapSize = 1 << 16;

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (SharpFuzz.Common.Trace.SharedMem == null)
            SharpFuzz.Common.Trace.SharedMem = (byte*)NativeMemory.AllocZeroed(MapSize);
    }
}
