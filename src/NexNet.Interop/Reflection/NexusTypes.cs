using System.Collections.Concurrent;
using System.Reflection;

namespace NexNet.Interop.Reflection;

/// <summary>Finds the interface members and generated scaffolding types behind remote descriptors.</summary>
internal static class NexusTypes
{
    private static readonly ConcurrentDictionary<(Type, string, string), MethodInfo> Methods = new();
    private static readonly ConcurrentDictionary<(Type, string, string), PropertyInfo> Properties = new();
    private static readonly ConcurrentDictionary<Type, Type> RemoteProxyTypes = new();
    private static readonly Lazy<IReadOnlyDictionary<string, Type>> FacadeTypes = new(FindFacades);

    /// <summary>The interfaces of <paramref name="type"/> named <paramref name="interfaceName"/>, with what they inherit.</summary>
    private static IEnumerable<Type> InterfaceAndBases(Type type, string interfaceName)
    {
        var root = type.GetInterfaces().FirstOrDefault(i => i.FullName == interfaceName)
                   ?? throw new InvalidOperationException($"{type} does not implement {interfaceName}.");
        return root.GetInterfaces().Prepend(root);
    }

    public static MethodInfo FindMethod(Type implementingType, RemoteMethod method)
        => Methods.GetOrAdd((implementingType, method.InterfaceName, method.Name), static key =>
        {
            var found = InterfaceAndBases(key.Item1, key.Item2)
                .SelectMany(i => i.GetMethods())
                .Where(m => m.Name == key.Item3)
                .ToArray();
            return found.Length == 1
                ? found[0]
                : throw new InvalidOperationException($"{key.Item2} has {found.Length} methods named {key.Item3}.");
        });

    public static PropertyInfo FindProperty(Type implementingType, RemoteCollection collection)
        => Properties.GetOrAdd((implementingType, collection.InterfaceName, collection.Name), static key =>
            InterfaceAndBases(key.Item1, key.Item2).Select(i => i.GetProperty(key.Item3)).FirstOrDefault(p => p != null)
            ?? throw new InvalidOperationException($"{key.Item2} has no collection {key.Item3}."));

    /// <summary>
    /// The generated remote proxy type for <paramref name="requested"/>: the type itself when it is a remote proxy, or the
    /// remote proxy implementing it when it is a server nexus interface.
    /// </summary>
    public static Type GetRemoteProxyType(Type requested)
        => RemoteProxyTypes.GetOrAdd(requested, static t =>
        {
            if (!t.IsInterface && typeof(IRemoteProxy).IsAssignableFrom(t))
                return t;

            if (t.IsInterface)
            {
                foreach (var type in LoadedTypes())
                {
                    if (!type.IsAbstract && typeof(IRemoteProxy).IsAssignableFrom(type) && t.IsAssignableFrom(type))
                        return type;
                }
            }

            throw new InvalidOperationException(
                $"No generated remote proxy for {t}. Add [assembly: NexusTestScaffolding<TServer, TClient>] for its nexus pair.");
        });

    /// <summary>Creates the remote proxy <paramref name="proxyType"/> over <paramref name="invoker"/> (its static Create).</summary>
    public static object CreateRemoteProxy(Type proxyType, IRemoteInvoker invoker)
    {
        var create = proxyType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, [typeof(IRemoteInvoker)])
                     ?? throw new InvalidOperationException($"{proxyType} has no static Create(IRemoteInvoker).");
        return create.Invoke(null, [invoker])!;
    }

    /// <summary>The generated script facade type of the client nexus interface <paramref name="clientInterface"/>, or null.</summary>
    public static Type? FindScriptFacade(string clientInterface)
        => FacadeTypes.Value.GetValueOrDefault(clientInterface);

    private static IReadOnlyDictionary<string, Type> FindFacades()
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in LoadedTypes())
        {
            if (type.IsAbstract || !typeof(ScriptFacade).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                continue;

            var facade = (ScriptFacade)Activator.CreateInstance(type)!;
            result.TryAdd(facade.ClientInterface, type);
        }

        return result;
    }

    private static IEnumerable<Type> LoadedTypes()
    {
        // The scaffolding of the shared nexuses lives in this assembly; test assemblies may add their own.
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.GetReferencedAssemblies().Any(r => r.Name == "NexNet.Interop.Abstractions"))
            .Prepend(typeof(NexusTypes).Assembly)
            .Distinct();

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray()!;
            }

            foreach (var type in types)
                yield return type;
        }
    }
}
