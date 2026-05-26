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
            if (IsSerializableParameter(parameters[i].ParameterType))
                result.Add(allMatchers[i]);
        }
        return result;
    }

    private static bool IsSerializableParameter(Type t)
    {
        if (t.FullName == "System.Threading.CancellationToken") return false;
        var ns = t.Namespace;
        if (ns is not null && ns.StartsWith("NexNet.Pipes")) return false;
        return true;
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

        var recentIds = _recorder.Snapshot()
            .Reverse()
            .Take(10)
            .Select(r => $"#{r.MethodId}")
            .ToArray();
        if (recentIds.Length > 0)
        {
            sb.Append(" Recent recorded methodIds (most-recent first): ");
            sb.Append(string.Join(", ", recentIds));
        }
        return sb.ToString();
    }
}
