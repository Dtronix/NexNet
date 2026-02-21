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
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MixedPermissionEnumTypes = new(
        id: "NEXNET026",
        title: "All NexusAuthorize attributes must use the same permission enum type",
        messageFormat: "The nexus '{0}' uses [NexusAuthorize] with mixed permission enum types. All attributes must use the same TPermission type.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}


