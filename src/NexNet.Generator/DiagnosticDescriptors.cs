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
}
