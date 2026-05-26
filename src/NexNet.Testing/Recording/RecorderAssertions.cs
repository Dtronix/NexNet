using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Testing.Recording;

/// <summary>
/// Shared assertion logic used by both the host-side (server recorder) and the client-side
/// (per-client recorder) assertion APIs. Holds a method-id map cache keyed by interface type.
/// Stateless apart from that cache — instances can be reused freely across calls.
/// </summary>
internal sealed class RecorderAssertions
{
    private readonly InvocationRecorder _recorder;
    private readonly Dictionary<Type, Dictionary<MethodInfo, ushort>> _methodIdMapsByInterface = new();

    public RecorderAssertions(InvocationRecorder recorder)
    {
        _recorder = recorder;
    }

    public void AssertReceived<TInterface>(Expression<Action<TInterface>> expression, int times)
    {
        var matches = FindMatches(expression);
        if (matches.Count != times)
            throw new NexusAssertionException(
                BuildMismatchMessage(expression, expected: times, observed: matches.Count));
    }

    public void AssertNotReceived<TInterface>(Expression<Action<TInterface>> expression)
    {
        var matches = FindMatches(expression);
        if (matches.Count > 0)
            throw new NexusAssertionException(
                BuildMismatchMessage(expression, expected: 0, observed: matches.Count));
    }

    public async Task WaitFor<TInterface>(
        Expression<Action<TInterface>> expression,
        TimeSpan? timeout)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            if (FindMatches(expression).Count > 0) return;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException(
                    $"WaitFor timed out after {timeout?.TotalSeconds ?? 5} seconds.");

            using var cts = new CancellationTokenSource(remaining);
            try { await _recorder.WaitForChangeAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* re-check on next loop iteration */ }
        }
    }

    private Dictionary<MethodInfo, ushort> GetMethodIdMap(Type interfaceType)
    {
        lock (_methodIdMapsByInterface)
        {
            if (!_methodIdMapsByInterface.TryGetValue(interfaceType, out var map))
            {
                map = MethodIdMap.Build(interfaceType);
                _methodIdMapsByInterface[interfaceType] = map;
            }
            return map;
        }
    }

    private IReadOnlyList<InvocationRecord> FindMatches<TInterface>(Expression<Action<TInterface>> expression)
    {
        var (method, matchers) = ExpressionParser.Parse(expression);
        var idMap = GetMethodIdMap(typeof(TInterface));
        if (!idMap.TryGetValue(method, out var methodId))
            return Array.Empty<InvocationRecord>();

        var serializableMatchers = SelectSerializableMatchers(method, matchers);

        var matches = new List<InvocationRecord>();
        foreach (var record in _recorder.Snapshot())
        {
            if (record.MethodId != methodId) continue;
            if (!ArgsMatch(method, serializableMatchers, record.Arguments)) continue;
            matches.Add(record);
        }
        return matches;
    }

    private static IReadOnlyList<ArgMatcher> SelectSerializableMatchers(
        MethodInfo method, IReadOnlyList<ArgMatcher> allMatchers)
    {
        var parameters = method.GetParameters();
        var result = new List<ArgMatcher>(allMatchers.Count);
        for (int i = 0; i < parameters.Length; i++)
        {
            if (ArgumentDeserializer.IsSerializableParameter(parameters[i].ParameterType))
                result.Add(allMatchers[i]);
        }
        return result;
    }

    private static bool ArgsMatch(MethodInfo method, IReadOnlyList<ArgMatcher> matchers, ReadOnlyMemory<byte> argsBytes)
    {
        if (matchers.Count == 0) return true;
        var values = ArgumentDeserializer.Deserialize(method, argsBytes);
        if (values.Length != matchers.Count) return false;
        for (int i = 0; i < values.Length; i++)
            if (!matchers[i].Matches(values[i])) return false;
        return true;
    }

    private string BuildMismatchMessage<TInterface>(
        Expression<Action<TInterface>> expression,
        int expected,
        int observed)
    {
        var sb = new StringBuilder();
        sb.Append("Expected ").Append(expected).Append(" invocation(s) matching '")
          .Append(expression).Append("', observed ").Append(observed).Append('.');

        var recent = _recorder.Snapshot().Reverse().Take(10).ToArray();
        if (recent.Length > 0)
        {
            // Build a methodId -> MethodInfo reverse map so we can deserialize the arg bytes
            // and render the actual recorded values alongside the methodIds. Falls back to the
            // bare methodId when no MethodInfo is known (e.g., a method id from a different
            // interface than the one currently being asserted against).
            var idToMethod = new Dictionary<ushort, MethodInfo>();
            var interfaceMap = GetMethodIdMap(typeof(TInterface));
            foreach (var (method, id) in interfaceMap)
                idToMethod[id] = method;

            sb.Append(" Recent recorded invocations (most-recent first):");
            for (int i = 0; i < recent.Length; i++)
            {
                var record = recent[i];
                sb.Append(' ');
                sb.Append('[').Append(i).Append("] ");
                if (idToMethod.TryGetValue(record.MethodId, out var method))
                {
                    sb.Append(method.Name).Append('(');
                    var argText = TryRenderArgs(method, record.Arguments);
                    sb.Append(argText);
                    sb.Append(')');
                }
                else
                {
                    sb.Append("#").Append(record.MethodId).Append("(unknown-method)");
                }
                if (i < recent.Length - 1) sb.Append(';');
            }
        }
        return sb.ToString();
    }

    private static string TryRenderArgs(MethodInfo method, ReadOnlyMemory<byte> argsBytes)
    {
        try
        {
            var values = ArgumentDeserializer.Deserialize(method, argsBytes);
            if (values.Length == 0) return string.Empty;
            return string.Join(", ", values.Select(FormatArgValue));
        }
        catch (Exception ex)
        {
            // A deserialize failure here is not the user's main bug — it's a side-effect of
            // surfacing the diagnostic. Show the exception type instead of throwing.
            return $"<args undecodable: {ex.GetType().Name}>";
        }
    }

    private static string FormatArgValue(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        _ => value.ToString() ?? value.GetType().Name,
    };
}
