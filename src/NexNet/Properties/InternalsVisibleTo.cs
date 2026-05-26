// Per plan §2, NexNet.Testing accesses internal hooks (IInvocationInterceptor, IPipeFactory,
// OnAuthenticateOverride, etc.) defined in this assembly. The grant lives in source rather than
// MSBuild metadata so a code-search for "InternalsVisibleTo" surfaces it next to the rest of
// the source. The other grants (IntegrationTests, Asp, Benchmarks) remain in NexNet.csproj
// because they predate this convention.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("NexNet.Testing")]
