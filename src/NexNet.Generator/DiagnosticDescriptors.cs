using Microsoft.CodeAnalysis;
#pragma warning disable RS2008

namespace NexNet.Generator;
internal static class DiagnosticDescriptors
{
    const string Category = "GenerateNexNet";

    public static readonly DiagnosticDescriptor MustBePartial = new(
        id: "NEXNET001",
        title: "Nexus class must be partial",
        messageFormat: "The Nexus object '{0}' must be partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MustNotBeAbstractOrInterface = new(
        id: "NEXNET002",
        title: "Nexus must not be abstract nor an interface",
        messageFormat: "The Nexus object '{0}' must not be abstract nor an interface",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MustReturnVoidOrValueTask = new(
        id: "NEXNET003",
        title: "Nexus method must return either void, ValueTask or ValueTask<T>",
        messageFormat: "The Nexus object '{0}' must return either void, ValueTask or ValueTask<T>",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NexusMustNotBeGeneric = new(
        id: "NEXNET004",
        title: "Nexus must not be generic",
        messageFormat: "The Nexus '{0}' must not be generic",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvokeMethodCoreReservedMethodName = new(
        id: "NEXNET005",
        title: "Nexus method InvokeMethodCore is reserved",
        messageFormat: "The Nexus object '{0}' must not have a method InvokeMethodCore defined as it is reserved",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor NestedNotAllow = new(
        id: "NEXNET006",
        title: "Nexus must not be nested type",
        messageFormat: "The Nexus object '{0}' must be not nested type",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicatedMethodId = new(
        id: "NEXNET007",
        title: "Nexus must not have duplicate MethodIds",
        messageFormat: "The Nexus method '{0}' must not reuse a method id used previously in the interface",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidReturnValue = new(
        id: "NEXNET008",
        title: "Nexus method with invalid return type",
        messageFormat: "The Nexus method '{0}' have a return type of ValueTask, ValueTask<T> or void",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidCancellationToken = new(
        id: "NEXNET009",
        title: "Nexus method cancellation token invalid usage",
        messageFormat: "The Nexus method '{0}' must use the cancellation token at the end of the parameters",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TooManyCancellationTokens = new(
        id: "NEXNET010",
        title: "Nexus method cancellation token invalid usage",
        messageFormat: "The Nexus method '{0}' has multiple cancellation tokens when only one is allowed",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CancellationTokenOnVoid = new(
        id: "NEXNET011",
        title: "Nexus method can't be void and support cancellation tokens",
        messageFormat: "The Nexus method '{0}' can't be void and use a cancellation token. Must return ValueTask or ValueTask<T> to use a cancellation token.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TooManyPipes = new(
        id: "NEXNET012",
        title: "Nexus method only supports one INexusDuplexPipe",
        messageFormat: "The Nexus method '{0}' has multiple INexusDuplexPipe parameters when only one is allowed",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PipeOnVoidOrReturnTask = new(
        id: "NEXNET013",
        title: "Nexus method can't be void nor ValueTask<T> and support INexusDuplexPipe transportation",
        messageFormat: "The Nexus method '{0}' can't be void nor ValueTask<T> and have a INexusDuplexPipe parameter.  Must return ValueTask to use INexusDuplexPipe.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SeparatePoolOnPipeMethod = new(
        id: "NEXNET039",
        title: "SeparatePool has no effect on a pipe or channel method",
        messageFormat: "SeparatePool has no effect on the Nexus method '{0}': a method with a pipe or channel parameter frees its execution slot once the pipe is registered",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PipeOnMethodWithCancellationToken = new(
        id: "NEXNET014",
        title: "Nexus method support INexusDuplexPipe and CancellationToken on the same method",
        messageFormat: "The Nexus method '{0}' can't contain a INexusDuplexPipe and CancellationToken parameters on the same method",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor CollectionUnknownType = new(
        id: "NEXNET015",
        title: "Nexus collection type unsupported",
        messageFormat: "The collection type '{0}' is unsupported. Use INexusList<T> instead.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor CollectionUnknownMode = new(
        id: "NEXNET016",
        title: "Nexus collection mode type is unsupported",
        messageFormat: "The collection mode set for '{0}' is unsupported. Use ServerToClient, Relay or BiDirectional.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor CollectionAttributeMissing = new(
        id: "NEXNET017",
        title: "Nexus collection property is unconfigured",
        messageFormat: "The collection '{0}' must have an attached NexusCollectionAttribute to set the collection mode",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor CollectionCanNotBeOnClient = new(
        id: "NEXNET018",
        title: "Nexus collection property is only allowed on server",
        messageFormat: "The collection '{0}' is not allowed to be on a client nexus",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor VersionHashLockMismatch = new(
        id: "NEXNET019",
        title: "NexusVersion invalid hash lock",
        messageFormat: "The NexusVersion on '{0}' does not match the calculated HashLock of '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor VersionHashLockNotSet = new(
        id: "NEXNET020",
        title: "NexusVersion HashLock not set",
        messageFormat: "The NexusVersion on '{0}' does not specify the calculated HashLock of '{1}'. Set this value prior to shipping your API to ensure API changes to no accidentally occur.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor AllInterfacesMustBeVersioning = new(
        id: "NEXNET021",
        title: "NexusVersion is enabled but not present on all interfaces",
        messageFormat: "The interface '{0}' does not have a NexusVersionAttribute set while other chained interface(s) do. Set NexusVersionAttribute for the '{0}' interface.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor AllMethodsIdsShallBeSetForVersioningNexuses = new(
        id: "NEXNET022",
        title: "NexusMethodAttribute is not set on all methods & collections while versioning",
        messageFormat: "The method '{0}' does not have a NexusMethodAttribute as required while versioning. Set NexusMethodAttribute for the '{0}' method.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor AllMethodsIdsShallNotBe0ForVersioningNexuses = new(
        id: "NEXNET023",
        title: "NexusMethodAttribute is not set on all methods and or collections while versioning",
        messageFormat: "The member '{0}' NexusMethodAttribute.MethodId value is set to '{1}' which is invalid while versioning. Set NexusMethodAttribute.MethodId for the '{0}' method to a non-zero value.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AuthorizeOnClientNexus = new(
        id: "NEXNET024",
        title: "NexusAuthorize is only supported on server nexuses",
        messageFormat: "The method '{0}' uses [NexusAuthorize] but the nexus is a client nexus. Authorization is only supported on server nexuses.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AuthorizeWithoutOnAuthorize = new(
        id: "NEXNET025",
        title: "NexusAuthorize used but OnAuthorize is not overridden",
        messageFormat: "The nexus '{0}' uses [NexusAuthorize] but does not override OnAuthorize. All invocations will be allowed by default.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MixedPermissionEnumTypes = new(
        id: "NEXNET026",
        title: "All NexusAuthorize attributes must use the same permission enum type",
        messageFormat: "The nexus '{0}' uses [NexusAuthorize] with mixed permission enum types. All attributes must use the same TPermission type.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AuthorizeEnumUnderlyingTypeIncompatible = new(
        id: "NEXNET027",
        title: "NexusAuthorize permission enum must be backed by int",
        messageFormat: "The method '{0}' uses [NexusAuthorize] with a permission enum that is not backed by int. Permission values are stored as Int32. Use an enum backed by int (the default).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ------------------------------------------------------------------ Serialization

    public static readonly DiagnosticDescriptor TypeNotSerializable = new(
        id: "NEXNET028",
        title: "Type is not serializable",
        messageFormat: "The type '{0}' used by '{1}' has no NexNet formatter. Annotate it with [NexusObject], register a formatter with [assembly: NexusFormatter<TFormatter, T>] or use a supported built-in type.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateNexusKey = new(
        id: "NEXNET029",
        title: "Duplicate NexusKey",
        messageFormat: "The type '{0}' has a duplicate or negative [NexusKey] on member '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingNexusKey = new(
        id: "NEXNET030",
        title: "Public member requires NexusKey or NexusIgnore",
        messageFormat: "The public member '{1}' of [NexusObject] type '{0}' must have [NexusKey] or [NexusIgnore]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnassignableMember = new(
        id: "NEXNET031",
        title: "Keyed member cannot be assigned",
        messageFormat: "The member '{1}' of [NexusObject] type '{0}' cannot be assigned during deserialization (no setter, init accessor, matching constructor parameter or backing field)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoUsableConstructor = new(
        id: "NEXNET032",
        title: "No usable constructor",
        messageFormat: "The [NexusObject] type '{0}' has no usable constructor. Provide an accessible parameterless constructor, a single accessible constructor, or mark one with [NexusConstructor].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidUnionCase = new(
        id: "NEXNET033",
        title: "Invalid union case",
        messageFormat: "The union '{0}' has a duplicate tag or a case type that does not derive from it: '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AbstractWithoutUnion = new(
        id: "NEXNET034",
        title: "Abstract NexusObject requires union cases",
        messageFormat: "The abstract or interface [NexusObject] type '{0}' must declare cases with [NexusUnion<T>(tag)]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NexusObjectNotAccessible = new(
        id: "NEXNET036",
        title: "NexusObject type is not accessible",
        messageFormat: "The [NexusObject] type '{0}' is not accessible to generated code. Make it public or internal (with InternalsVisibleTo across assemblies).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor LargeKeyGap = new(
        id: "NEXNET037",
        title: "Large NexusKey gap",
        messageFormat: "The [NexusObject] type '{0}' has {1} unused key positions, each written as nil",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ChannelTypeNotSerializable = new(
        id: "NEXNET038",
        title: "Channel type has no formatter",
        messageFormat: "The channel type '{0}' has no NexNet formatter. Annotate it with [NexusObject] or declare it with [assembly: NexusSerializable<T>].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateMethodName = new(
        id: "NEXNET042",
        title: "Duplicate nexus method name",
        messageFormat: "Nexus interface '{0}' declares more than one method named '{1}'; method names must be unique across the interface and its inherited interfaces",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TargetsOnClientNexus = new(
        id: "NEXNET043",
        title: "Targets set on a client nexus",
        messageFormat: "The client nexus '{0}' sets Targets. Set Targets on the server nexus, or export the client with [assembly: NexusClientExport<TServer, TClient>(Targets = ...)].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidTargetFormatter = new(
        id: "NEXNET048",
        title: "Invalid NexusTargetFormatter",
        messageFormat: "The [NexusTargetFormatter] for '{0}' is invalid: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TargetTypeUnsupported = new(
        id: "NEXNET040",
        title: "Type not representable in a target language",
        messageFormat: "The type {0} cannot be represented in {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TargetNameCollision = new(
        id: "NEXNET041",
        title: "Name collision in a target language",
        messageFormat: "Name collision ({0}): {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TargetWithoutOutput = new(
        id: "NEXNET044",
        title: "Target has no output directory",
        messageFormat: "'{0}' opts in to the {1} target, but no output directory is configured for it, so no {1} code is generated. Set NexNet{1}OutputDir or add a NexNetTarget item with an OutputDir.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidTargetConfiguration = new(
        id: "NEXNET045",
        title: "Invalid target configuration",
        messageFormat: "Invalid NexNet target configuration: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TargetReferenceKey = new(
        id: "NEXNET046",
        title: "Set or dictionary key compares by reference in a target language",
        messageFormat: "{0} uses a set element or dictionary key that compares by reference in {1}; a lookup with an equal value does not find the entry",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TargetFileCollision = new(
        id: "NEXNET047",
        title: "Target output file collision",
        messageFormat: "Output file collision ({0}): {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidTestScaffolding = new(
        id: "NEXNET049",
        title: "Invalid NexusTestScaffolding",
        messageFormat: "The C# test scaffolding of '{0}' cannot be generated: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static DiagnosticDescriptor? FindTargetDescriptor(string id) => id switch
    {
        "NEXNET040" => TargetTypeUnsupported,
        "NEXNET041" => TargetNameCollision,
        "NEXNET042" => DuplicateMethodName,
        "NEXNET044" => TargetWithoutOutput,
        "NEXNET045" => InvalidTargetConfiguration,
        "NEXNET046" => TargetReferenceKey,
        "NEXNET047" => TargetFileCollision,
        "NEXNET048" => InvalidTargetFormatter,
        "NEXNET049" => InvalidTestScaffolding,
        _ => null
    };

    public static DiagnosticDescriptor? FindSerializationDescriptor(string id) => id switch
    {
        "NEXNET028" => TypeNotSerializable,
        "NEXNET029" => DuplicateNexusKey,
        "NEXNET030" => MissingNexusKey,
        "NEXNET031" => UnassignableMember,
        "NEXNET032" => NoUsableConstructor,
        "NEXNET033" => InvalidUnionCase,
        "NEXNET034" => AbstractWithoutUnion,
        "NEXNET036" => NexusObjectNotAccessible,
        "NEXNET037" => LargeKeyGap,
        "NEXNET038" => ChannelTypeNotSerializable,
        _ => null
    };
}


