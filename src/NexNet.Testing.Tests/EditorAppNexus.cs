using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Invocation;
using NexNet.Pipes;

namespace NexNet.Testing.Tests;

public enum DocPermission
{
    Read,
    Write,
    Admin,
}

[MemoryPackable]
public partial record struct EditOp(int Position, string Inserted);

internal sealed class DocState
{
    public byte[]? Attachment;
    public ConcurrentQueue<EditOp> Edits { get; } = new();
}

[Nexus<IEditorServerNexus, IEditorClientNexus>(NexusType = NexusType.Server)]
internal partial class EditorServerNexus : ServerNexusBase<EditorServerNexus.ClientProxy>, IEditorServerNexus
{
    public int PingCount;

    // Cross-session shared state. Tests MUST clear via ResetAll() at the top of each [Test].
    public static readonly ConcurrentDictionary<string, DocState> Documents = new();
    public static readonly ConcurrentDictionary<string, ConcurrentDictionary<long, string>> ActiveEditors = new();
    public static byte[]? LastUploadedBytes;
    public static List<string>? LastCollectedItems;

    // Resets cross-fixture process-global state. PingCount is intentionally NOT touched —
    // it is per-instance (each test gets its own EditorServerNexus via the factory) and
    // doesn't bleed across tests. The Documents / ActiveEditors / LastUploadedBytes /
    // LastCollectedItems statics persist across nexus instances and MUST be cleared by
    // every test fixture that exercises these fields, regardless of whether the test
    // reads them directly. Tests assume sequential fixture execution; enabling
    // [assembly: Parallelizable(ParallelScope.Fixtures)] would race this state.
    public static void ResetAll()
    {
        Documents.Clear();
        ActiveEditors.Clear();
        LastUploadedBytes = null;
        LastCollectedItems = null;
    }

    public ValueTask<int> Ping(int value)
    {
        Interlocked.Increment(ref PingCount);
        return ValueTask.FromResult(value);
    }

    public ValueTask Notify(string message) => ValueTask.CompletedTask;

    public async ValueTask Upload(INexusDuplexPipe pipe)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await pipe.Input.ReadAsync();
            foreach (var segment in result.Buffer)
                ms.Write(segment.Span);
            pipe.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
        LastUploadedBytes = ms.ToArray();
    }

    public async ValueTask Download(INexusDuplexPipe pipe, int byteCount)
    {
        var buf = new byte[byteCount];
        for (int i = 0; i < byteCount; i++) buf[i] = (byte)i;
        await pipe.Output.WriteAsync(buf);
        await pipe.CompleteAsync();
    }

    public async ValueTask CollectStrings(INexusDuplexPipe pipe)
    {
        var reader = await pipe.GetChannelReader<string>();
        var items = new List<string>();
        await foreach (var item in reader)
            items.Add(item);
        LastCollectedItems = items;
    }

    public async ValueTask PublishStrings(INexusDuplexPipe pipe, string[] items)
    {
        var writer = await pipe.GetChannelWriter<string>();
        foreach (var item in items)
            await writer.WriteAsync(item);
        await writer.CompleteAsync();
    }

    public async ValueTask OpenDocument(string docId)
    {
        var name = Context.Identity?.DisplayName ?? "anonymous";
        await Context.Groups.AddAsync(GroupName(docId));
        var registry = ActiveEditors.GetOrAdd(docId, _ => new ConcurrentDictionary<long, string>());
        registry[Context.Id] = name;
        Documents.GetOrAdd(docId, _ => new DocState());
        await Context.Clients.GroupExceptCaller(GroupName(docId)).EditorJoined(name);
    }

    public async ValueTask LeaveDocument(string docId)
    {
        var name = Context.Identity?.DisplayName ?? "anonymous";
        await Context.Clients.GroupExceptCaller(GroupName(docId)).EditorLeft(name);
        await Context.Groups.RemoveAsync(GroupName(docId));
        if (ActiveEditors.TryGetValue(docId, out var registry))
            registry.TryRemove(Context.Id, out _);
    }

    [NexusAuthorize<DocPermission>(DocPermission.Write)]
    public async ValueTask SaveDraft(string docId, string content)
    {
        var name = Context.Identity?.DisplayName ?? "anonymous";
        await Context.Clients.Group(GroupName(docId)).DraftSaved(name, content);
    }

    public async ValueTask Whisper(long targetSessionId, string text)
    {
        var name = Context.Identity?.DisplayName ?? "anonymous";
        await Context.Clients.Client(targetSessionId).WhisperReceived(name, text);
    }

    [NexusAuthorize<DocPermission>(DocPermission.Admin)]
    public async ValueTask BroadcastSystemAnnouncement(string message)
    {
        await Context.Clients.All.SystemAnnouncement(message);
    }

    public async ValueTask<string[]> ListActiveEditors(string docId, CancellationToken cancellationToken)
    {
        // Observable async point so callers passing a cancellable CancellationToken can
        // actually observe cancellation. The framework only sends a cancel signal when the
        // client-side CT FIRES during the call (pre-cancelled tokens are not short-circuited
        // by the proxy), so the server needs an awaiting point long enough for that signal
        // to arrive. Guarded on CanBeCanceled so happy-path callers (CancellationToken.None)
        // don't pay the latency tax.
        if (cancellationToken.CanBeCanceled)
            await Task.Delay(200, cancellationToken);
        if (!ActiveEditors.TryGetValue(docId, out var registry))
            return Array.Empty<string>();
        return registry.Values.ToArray();
    }

    public async ValueTask UploadAttachment(string docId, INexusDuplexPipe pipe)
    {
        var doc = Documents.GetOrAdd(docId, _ => new DocState());
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await pipe.Input.ReadAsync();
            foreach (var segment in result.Buffer)
                ms.Write(segment.Span);
            pipe.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
        doc.Attachment = ms.ToArray();
    }

    public async ValueTask StreamEdits(string docId, INexusDuplexChannel<EditOp> channel)
    {
        var doc = Documents.GetOrAdd(docId, _ => new DocState());
        var reader = await channel.GetReaderAsync();
        await foreach (var op in reader)
            doc.Edits.Enqueue(op);
    }

    // Policy: AND-semantics over requiredPermissions — every declared permission must be
    // present on the identity, otherwise Unauthorized. This is a per-app choice, not a
    // framework default — the framework hands you the int[] of required permissions and
    // OnAuthorize decides how to interpret them. An OR-semantics policy would just flip the
    // loop to "any match → Allowed". Marker-only [NexusAuthorize<TPermission>()] (empty
    // requiredPermissions) is currently treated as "TestIdentity sufficient" since the for
    // loop is skipped entirely.
    protected override ValueTask<AuthorizeResult> OnAuthorize(
        ServerSessionContext<EditorServerNexus.ClientProxy> context,
        int methodId,
        string methodName,
        ReadOnlyMemory<int> requiredPermissions)
    {
        if (context.Identity is not TestIdentity id)
            return new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
        var span = requiredPermissions.Span;
        for (int i = 0; i < span.Length; i++)
        {
            var roleName = ((DocPermission)span[i]).ToString();
            if (!id.IsInRole(roleName))
                return new ValueTask<AuthorizeResult>(AuthorizeResult.Unauthorized);
        }
        return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
    }

    private static string GroupName(string docId) => $"doc-{docId}";
}

[Nexus<IEditorClientNexus, IEditorServerNexus>(NexusType = NexusType.Client)]
internal partial class EditorClientNexus : ClientNexusBase<EditorClientNexus.ServerProxy>, IEditorClientNexus
{
    public ValueTask DraftSaved(string author, string content) => ValueTask.CompletedTask;
    public ValueTask EditorJoined(string author) => ValueTask.CompletedTask;
    public ValueTask EditorLeft(string author) => ValueTask.CompletedTask;
    public ValueTask WhisperReceived(string from, string text) => ValueTask.CompletedTask;
    public ValueTask SystemAnnouncement(string message) => ValueTask.CompletedTask;
}

internal partial interface IEditorServerNexus
{
    ValueTask<int> Ping(int value);
    ValueTask Notify(string message);
    ValueTask Upload(INexusDuplexPipe pipe);
    ValueTask Download(INexusDuplexPipe pipe, int byteCount);
    ValueTask CollectStrings(INexusDuplexPipe pipe);
    ValueTask PublishStrings(INexusDuplexPipe pipe, string[] items);
    ValueTask OpenDocument(string docId);
    ValueTask LeaveDocument(string docId);
    ValueTask SaveDraft(string docId, string content);
    ValueTask Whisper(long targetSessionId, string text);
    ValueTask BroadcastSystemAnnouncement(string message);
    ValueTask<string[]> ListActiveEditors(string docId, CancellationToken cancellationToken);
    ValueTask UploadAttachment(string docId, INexusDuplexPipe pipe);
    ValueTask StreamEdits(string docId, INexusDuplexChannel<EditOp> channel);
}

internal partial interface IEditorClientNexus
{
    ValueTask DraftSaved(string author, string content);
    ValueTask EditorJoined(string author);
    ValueTask EditorLeft(string author);
    ValueTask WhisperReceived(string from, string text);
    ValueTask SystemAnnouncement(string message);
}
