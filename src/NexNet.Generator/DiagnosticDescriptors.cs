using Microsoft.CodeAnalysis;

namespace NexNet.Generator;

internal static class DiagnosticDescriptors
{
    const string Category = "GenerateNexNet";

    public static readonly DiagnosticDescriptor MustBePartial = new(
        id: "NEXNET001",
        title: "NexNetHub class must be partial",
        messageFormat: "The NexNetHub object '{0}' must be partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MustNotBeAbstractOrInterface = new(
        id: "NEXNET002",
        title: "NexNetHub must not be abstract nor an interface",
        messageFormat: "The NexNetHub object '{0}' must not be abstract nor an interface.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MustReturnVoidOrValueTask = new(
        id: "NEXNET003",
        title: "NexNetHub method must return either void, ValueTask or ValueTask<T>",
        messageFormat: "The NexNetHub object '{0}' must return either void, ValueTask or ValueTask<T>",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor HubMustNotBeGeneric = new(
        id: "NEXNET004",
        title: "NexNetHub hub must not be generic",
        messageFormat: "The NexNetHub hub '{0}' must not be generic",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvokeMethodCoreReservedMethodName = new(
        id: "NEXNET005",
        title: "NexNetHub method InvokeMethodCore is reserved",
        messageFormat: "The NexNetHub object '{0}' must not have a method InvokeMethodCore defined as it is reserved.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    
    public static readonly DiagnosticDescriptor NestedNotAllow = new(
        id: "NEXNET006",
        title: "NexNetHub hub must not be nested type",
        messageFormat: "The NexNetHub object '{0}' must be not nested type",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicatedMethodId = new(
        id: "NEXNET007",
        title: "NexNetHub must not have duplicate MethodIds",
        messageFormat: "The NexNetHub method '{0}' must not reuse a method id used previously in the interface.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidReturnValue = new(
        id: "NEXNET008",
        title: "NexNetHub method with invalid return type.",
        messageFormat: "The NexNetHub method '{0}' have a return type of ValueTask, ValueTask<T> or void.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidCancellationToken = new(
        id: "NEXNET009",
        title: "NexNetHub method cancellation token invalid usage.",
        messageFormat: "The NexNetHub method '{0}' must use the cancellation token at the end of the parameter list.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CancellationTokenOnVoid = new(
        id: "NEXNET010",
        title: "NexNetHub method can't be void and support cancellation tokens.",
        messageFormat: "The NexNetHub method '{0}' can't be void and use a cancellation token.  Must return ValueTask or ValueTask<T> to use a cancellation token.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
