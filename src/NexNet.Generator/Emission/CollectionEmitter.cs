using System.Text;
using NexNet.Generator.Models;

namespace NexNet.Generator.Emission;

/// <summary>
/// Emits collection-related code.
/// Works entirely with cached data records - no semantic model access.
/// </summary>
internal static class CollectionEmitter
{
    /// <summary>
    /// Gets the short string representation of the collection type.
    /// </summary>
    private static string GetCollectionTypeShortString(CollectionTypeValue collectionType) =>
        collectionType switch
        {
            CollectionTypeValue.List => "INexusList",
            _ => "INVALID"
        };

    /// <summary>
    /// Gets the full type string for the collection type.
    /// </summary>
    private static string GetCollectionTypeFullString(CollectionTypeValue collectionType) =>
        collectionType switch
        {
            CollectionTypeValue.List => "global::NexNet.Collections.Lists.INexusList",
            _ => "INVALID"
        };

    /// <summary>
    /// Gets the full type string for the collection mode.
    /// </summary>
    private static string GetCollectionModeFullTypeString(NexusCollectionMode mode) =>
        mode switch
        {
            NexusCollectionMode.ServerToClient => "global::NexNet.Collections.NexusCollectionMode.ServerToClient",
            NexusCollectionMode.BiDirectional => "global::NexNet.Collections.NexusCollectionMode.BiDirectional",
            NexusCollectionMode.Relay => "global::NexNet.Collections.NexusCollectionMode.Relay",
            _ => "INVALID"
        };

    /// <summary>
    /// Emit the code for collection invocation on the nexus.
    /// </summary>
    public static void EmitNexusInvocation(StringBuilder sb, CollectionData collection)
    {
        sb.Append(@"
                        var arguments = message.DeserializeArguments<global::System.ValueTuple<global::System.Byte>>();
                        duplexPipe = await methodInvoker.RegisterDuplexPipe(arguments.Item1).ConfigureAwait(false);
                        this.Context.Logger?.NexusLog($""Nexus ")
            .Append(GetCollectionTypeShortString(collection.CollectionType))
            .Append(" Collection connection Invocation: ")
            .Append(collection.Name)
            .Append(@" pipe = {arguments.Item1}"");

                        await global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.ICollectionStore>(this).StartCollection(")
            .Append(collection.Id).AppendLine(", duplexPipe);");
    }

    /// <summary>
    /// Emit the collection configuration code.
    /// </summary>
    public static void EmitCollectionConfigure(StringBuilder sb, CollectionData collection)
    {
        sb.Append("            manager.ConfigureList<")
            .Append(collection.ItemType)
            .Append(">(")
            .Append(collection.Id)
            .Append(", ")
            .Append(GetCollectionModeFullTypeString(collection.CollectionAttribute.Mode))
            .AppendLine(");");
    }

    /// <summary>
    /// Emit the proxy accessor property.
    /// </summary>
    public static void EmitProxyAccessor(StringBuilder sb, CollectionData collection)
    {
        sb.Append("            public ");
        sb.Append(GetCollectionTypeFullString(collection.CollectionType));

        if (collection.ItemType != null)
        {
            sb.Append("<").Append(collection.ItemType).Append(">");
        }

        sb.Append(" ");
        sb.Append(collection.Name)
            .Append(" => global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.IProxyInvoker>(this).");
        sb.Append(collection.CollectionType switch
        {
            CollectionTypeValue.List => "ProxyGetConfiguredNexusList<",
            _ => "INVALID"
        }).Append(collection.ItemType).Append(">(");

        sb.Append(collection.Id).AppendLine(");");
    }

    /// <summary>
    /// Emit the nexus accessor property.
    /// </summary>
    public static void EmitNexusAccessor(StringBuilder sb, CollectionData collection)
    {
        sb.Append("    public ");
        sb.Append(GetCollectionTypeFullString(collection.CollectionType));

        if (collection.ItemType != null)
        {
            sb.Append("<").Append(collection.ItemType).Append(">");
        }

        sb.Append(" ");
        sb.Append(collection.Name)
            .Append(" => global::System.Runtime.CompilerServices.Unsafe.As<global::NexNet.Invocation.ICollectionStore>(this).");
        sb.Append(collection.CollectionType switch
        {
            CollectionTypeValue.List => "GetList<",
            _ => "INVALID",
        }).Append(collection.ItemType).Append(">(");

        sb.Append(collection.Id).Append(");");
    }

    /// <summary>
    /// Gets a string representation of the collection for comments.
    /// </summary>
    public static string ToStringRepresentation(CollectionData collection)
    {
        var sb = SymbolUtilities.GetStringBuilder();

        // Use the short name description.
        if (collection.ItemType != null)
        {
            sb.Append(collection.ItemTypeSource).Append("(").Append(collection.Id).Append(");");
        }

        var stringMethod = sb.ToString();

        SymbolUtilities.ReturnStringBuilder(sb);

        return stringMethod;
    }
}
