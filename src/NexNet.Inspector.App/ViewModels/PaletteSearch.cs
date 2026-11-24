using System.Globalization;
using NexNet.Inspector.App.Controls.DataTable.Events;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Model.Palette;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>An open document as the palette's <c>@</c> scope lists it.</summary>
/// <param name="Title">The tab's title.</param>
/// <param name="IsLive">True for a live attach.</param>
/// <param name="Detail">Secondary text (state, frames).</param>
/// <param name="IsActive">True for the active document.</param>
public readonly record struct PaletteDocument(string Title, bool IsLive, string Detail, bool IsActive);

/// <summary>
/// The palette's search: scores every entity, frame, command and open document of the query's scope with the
/// <see cref="FuzzyMatcher"/>, keeps the best of each group, and makes rows for those alone. Grouped Entities →
/// Frames → Commands → Sessions (OQ14), fuzzy-ranked within each group, ties in capture order.
/// </summary>
/// <remarks>
/// The scan allocates nothing per candidate: each one's search text is written into one reused buffer (numbers
/// formatted in place, names are the schema's own strings) and scored on that span; the best ones are kept in
/// fixed arrays. Strings and rows are made only for the few results shown. Reads the session under its
/// <see cref="CaptureSession.Gate"/>. One instance per palette; not thread-safe.
/// </remarks>
public sealed class PaletteSearch
{
    /// <summary>Items per group when searching every scope.</summary>
    public const int GroupLimit = 5;

    /// <summary>Items when searching one scope (the list is virtualized).</summary>
    public const int ScopeLimit = 200;

    private const int Handshake = 0, Invocation = 1, Pipe = 2;
    private const int BestEntityBonus = 100_000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly char[] _key = new char[256];
    private readonly TopHits _hits = new(ScopeLimit);
    private readonly List<TraceFragment> _fragments = [];
    // The best entity hit when it is an invocation: what "Open … in Compare" opens on.
    private PaletteTarget _subjectTarget = PaletteTarget.None;

    /// <summary>Runs <paramref name="query"/> and returns its rows, group headings included.</summary>
    /// <param name="query">The parsed query.</param>
    /// <param name="session">The active document's session; null when none is open.</param>
    /// <param name="documents">The open documents, in tab order.</param>
    /// <param name="commands">The commands and their enabled state.</param>
    public List<PaletteRow> Run(in PaletteQuery query, CaptureSession? session, IReadOnlyList<PaletteDocument> documents, CommandRegistry commands)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(commands);
        List<PaletteRow> rows = [];
        string text = query.Text;
        bool all = query.Scope == PaletteScope.All;
        int limit = all ? GroupLimit : ScopeLimit;
        string? subject = null;
        FrameLink best = FrameLink.None;
        _subjectTarget = PaletteTarget.None;

        if (query.Scope == PaletteScope.Time)
        {
            AddTime(rows, text, session);
            return rows;
        }

        // An empty query lists only what is small enough to browse: never every frame or entity of a session.
        bool browse = text.Length > 0 || !all;
        if (session is not null && browse && query.Scope is PaletteScope.All or PaletteScope.Entities)
        {
            lock (session.Gate) subject = AddEntities(rows, text, session, limit, out best);
        }

        if (session is not null && browse && query.Scope is PaletteScope.All or PaletteScope.Frames)
        {
            lock (session.Gate) AddFrames(rows, text, session, limit, best);
        }

        if (query.Scope is PaletteScope.All or PaletteScope.Commands)
            // Suggestions go with other results only: a query that matches nothing at all says so.
            AddCommands(rows, text, commands, text.Length == 0 ? ScopeLimit : limit, suggest: all && text.Length > 0 && rows.Count > 0, subject);

        if (query.Scope is PaletteScope.All or PaletteScope.Sessions && (text.Length > 0 || !all))
            AddSessions(rows, text, documents, limit);

        return rows;
    }

    // Returns what a contextual command names ("invocation #812") when the best entity is an invocation, and that entity.
    private string? AddEntities(List<PaletteRow> rows, string query, CaptureSession session, int limit, out FrameLink best)
    {
        MessageAssembler assembler = session.Assembler;
        best = FrameLink.None;
        _hits.Reset(limit);
        if (assembler.Greetings.Length > 0) Offer(query, Key().Text("Handshake preamble greeting"), Id(Handshake, 0));

        ReadOnlySpan<InvocationRow> invocations = assembler.Invocations;
        for (int i = 0; i < invocations.Length; i++)
        {
            ref readonly InvocationRow row = ref invocations[i];
            KeyBuilder key = Key().Text("Invocation #").Number(row.InvocationId);
            if (row.RequestParsed && session.Decoder.TryGetMethod(row, out SchemaMethod? method)) key = key.Text(" ").Text(method.Name);
            Offer(query, key, Id(Invocation, i));
        }

        ReadOnlySpan<PipeRow> pipes = assembler.Pipes;
        for (int i = 0; i < pipes.Length; i++)
        {
            ref readonly PipeRow row = ref pipes[i];
            KeyBuilder key = Key();
            if (row.Content == PipeContent.Collection && row.Collection >= 0
                && session.Decoder.TryGetCollection(assembler.Collections[row.Collection].CollectionId, out SchemaCollection? collection))
                key = key.Text("Collection ").Text(collection.Name);
            else
                key = key.Text(PipeText.Title(row.Content, row.PipeId, row.Content == PipeContent.Channel ? session.Decoder.GetPipeLabel(i) : null))
                    .Text(" ").Text(OpenerName(session, row) ?? "");
            Offer(query, key, Id(Pipe, i));
        }

        if (_hits.Count == 0) return null;
        rows.Add(PaletteRow.Header(PaletteGroup.Entities));
        string? subject = null;
        for (int h = 0; h < _hits.Count; h++)
        {
            int id = _hits.IdAt(h);
            int index = id & 0xFFFFFF;
            PaletteRow row = (id >> 24) switch
            {
                Invocation => InvocationRow(session, index),
                Pipe => PipeItem(session, index),
                _ => HandshakeRow(session),
            };
            if (h == 0 && id >> 24 == Invocation)
            {
                subject = "invocation #" + assembler.Invocations[index].InvocationId.ToString(Invariant);
                best = FrameLink.None with { Entity = EntityKind.Invocation, EntityIndex = index };
                _subjectTarget = row.Target;
            }
            else if (h == 0 && id >> 24 == Pipe)
            {
                best = FrameLink.None with { Entity = EntityKind.Pipe, EntityIndex = index };
            }

            rows.Add(row);
        }

        return subject;
    }

    private PaletteRow InvocationRow(CaptureSession session, int index)
    {
        InvocationRow row = session.Assembler.Invocations[index];
        string label = "Invocation #" + row.InvocationId.ToString(Invariant);
        if (row.RequestParsed && session.Decoder.TryGetMethod(row, out SchemaMethod? method)) label += " · " + method.Name;
        MessageTrace trace = MessageTrace.Resolve(session, Selection.OfEntity(EntityKind.Invocation, index));
        return PaletteRow.Item(PaletteGroup.Entities, label, Span(session, trace), PaletteGlyph.Invocation,
            PaletteTarget.OfEntity(EntityKind.Invocation, index, trace.Start, trace.End));
    }

    private PaletteRow PipeItem(CaptureSession session, int index)
    {
        PipeRow row = session.Assembler.Pipes[index];
        MessageTrace trace = MessageTrace.Resolve(session, Selection.OfEntity(EntityKind.Pipe, index));
        PaletteTarget target = PaletteTarget.OfEntity(EntityKind.Pipe, index, trace.Start, trace.End);
        if (row.Content == PipeContent.Collection && row.Collection >= 0)
        {
            CollectionRow collection = session.Assembler.Collections[row.Collection];
            string name = session.Decoder.TryGetCollection(collection.CollectionId, out SchemaCollection? schema)
                ? schema.Name
                : PipeIds(row.PipeId);
            string messages = collection.MessageCount.ToString("N0", Invariant) + (collection.MessageCount == 1 ? " message" : " messages");
            return PaletteRow.Item(PaletteGroup.Entities, "Collection · " + name, messages + " · " + Lifecycle(row), PaletteGlyph.Collection, target);
        }

        string label = PipeText.Title(session, index);
        if (OpenerName(session, row) is { } opener) label += " · " + opener;
        string state = row.Content == PipeContent.Raw ? "raw · " + Lifecycle(row) : "on " + PipeText.CarriedBy(row.PipeId) + " · " + Lifecycle(row);
        return PaletteRow.Item(PaletteGroup.Entities, label, state + " · " + Span(session, trace),
            row.Content == PipeContent.Channel ? PaletteGlyph.Channel : PaletteGlyph.Pipe, target);
    }

    private static PaletteRow HandshakeRow(CaptureSession session)
    {
        MessageTrace trace = MessageTrace.Resolve(session, Selection.OfEntity(EntityKind.Greeting, 0));
        return PaletteRow.Item(PaletteGroup.Entities, "Handshake", "preambles and greetings · " + Span(session, trace), PaletteGlyph.Handshake,
            PaletteTarget.OfEntity(EntityKind.Greeting, 0, trace.Start, trace.End));
    }

    // The best entity's own frames rank first among the frames that match: the query most likely names that entity.
    private void AddFrames(List<PaletteRow> rows, string query, CaptureSession session, int limit, FrameLink best)
    {
        MessageAssembler assembler = session.Assembler;
        ReadOnlySpan<FrameKey> order = session.FrameOrder;
        _hits.Reset(limit);
        for (int i = 0; i < order.Length; i++)
        {
            FrameKey frameKey = order[i];
            if (!assembler.TryGetFrame(frameKey.Direction, frameKey.Seq, out FrameRecord frame)) continue;
            KeyBuilder key = Key().Text("#").Number(i + 1L).Text(" ").Text(TypeName(frame));
            FrameLink link = assembler.GetLink(frameKey.Direction, frameKey.Seq);
            key = AppendTarget(key, session, frame, link, withMethod: true);
            bool ofBest = best.Entity != EntityKind.None && link.Entity == best.Entity && link.EntityIndex == best.EntityIndex;
            Offer(query, key, i, ofBest ? BestEntityBonus : 0);
        }

        if (_hits.Count == 0) return;
        rows.Add(PaletteRow.Header(PaletteGroup.Frames));
        long frequency = Math.Max(1, session.Header.StopwatchFrequency);
        for (int h = 0; h < _hits.Count; h++)
        {
            int index = _hits.IdAt(h);
            FrameKey frameKey = order[index];
            assembler.TryGetFrame(frameKey.Direction, frameKey.Seq, out FrameRecord frame);
            FrameLink link = assembler.GetLink(frameKey.Direction, frameKey.Seq);
            string type = TypeName(frame);
            if (frame.Type == MessageType.MessageChunk && link.Message >= 0) type += ChunkSuffix(assembler, link.Message, frameKey);
            string target = AppendTarget(Key(), session, frame, link, withMethod: false).ToString();
            string label = "#" + (index + 1L).ToString("N0", Invariant) + " " + type + (target.Length > 0 ? " · " + target : "");
            string time = ((frame.StartTicks - session.StartTicks) * 1000.0 / frequency).ToString("N3", Invariant) + " ms";
            PipeContent pipe = link.Entity == EntityKind.Pipe && (uint)link.EntityIndex < (uint)assembler.Pipes.Length
                ? assembler.Pipes[link.EntityIndex].Content
                : PipeContent.Raw;
            rows.Add(PaletteRow.Item(PaletteGroup.Frames, label, time,
                frameKey.Direction == Core.Model.TapDirection.Inbound ? PaletteGlyph.Inbound : PaletteGlyph.Outbound,
                PaletteTarget.OfFrame(frameKey, frame.StartTicks), pipe: pipe));
        }
    }

    private void AddCommands(List<PaletteRow> rows, string query, CommandRegistry commands, int limit, bool suggest, string? subject)
    {
        IReadOnlyList<CommandDefinition> definitions = CommandRegistry.Definitions;
        _hits.Reset(limit);
        for (int i = 0; i < definitions.Count; i++)
        {
            CommandDefinition definition = definitions[i];
            if (definition.Command == AppCommand.CommandPalette) continue;
            Offer(query, Key().Text(definition.Title), i);
        }

        if (_hits.Count == 0 && !suggest) return;
        rows.Add(PaletteRow.Header(PaletteGroup.Commands));
        if (_hits.Count == 0)
        {
            foreach (AppCommand command in CommandRegistry.Suggestions) rows.Add(CommandItem(CommandRegistry.Get(command), commands, subject, _subjectTarget));
            return;
        }

        for (int h = 0; h < _hits.Count; h++) rows.Add(CommandItem(definitions[_hits.IdAt(h)], commands, subject, _subjectTarget));
    }

    private static PaletteRow CommandItem(CommandDefinition definition, CommandRegistry commands, string? subject, PaletteTarget subjectTarget)
    {
        // Compare names what it would open when the best entity hit is an invocation ("Open invocation #812 in Compare"),
        // and carries that entity, so running it selects the entity first and Compare opens on it.
        bool aboutSubject = definition.Command == AppCommand.OpenInCompare && subject is not null;
        string title = aboutSubject ? "Open " + subject + " in Compare" : definition.Title;
        PaletteTarget target = aboutSubject
            ? subjectTarget with { Kind = PaletteTargetKind.Command, Command = definition.Command }
            : PaletteTarget.OfCommand(definition.Command);
        return PaletteRow.Item(PaletteGroup.Commands, title, definition.Hint, PaletteGlyph.Command, target,
            definition.ShortcutText, commands.CanExecute(definition.Command));
    }

    private void AddSessions(List<PaletteRow> rows, string query, IReadOnlyList<PaletteDocument> documents, int limit)
    {
        _hits.Reset(limit);
        for (int i = 0; i < documents.Count; i++)
            Offer(query, Key().Text(documents[i].Title).Text(documents[i].IsLive ? " live" : " recording"), i);
        if (_hits.Count == 0) return;
        rows.Add(PaletteRow.Header(PaletteGroup.Sessions));
        for (int h = 0; h < _hits.Count; h++)
        {
            int index = _hits.IdAt(h);
            PaletteDocument document = documents[index];
            rows.Add(PaletteRow.Item(PaletteGroup.Sessions, document.Title, document.IsActive ? document.Detail + " · active" : document.Detail,
                document.IsLive ? PaletteGlyph.Live : PaletteGlyph.Recording, PaletteTarget.OfSession(index)));
        }
    }

    private static void AddTime(List<PaletteRow> rows, string text, CaptureSession? session)
    {
        rows.Add(PaletteRow.Header(PaletteGroup.Time));
        if (session is null)
        {
            rows.Add(PaletteRow.Item(PaletteGroup.Time, "No timeline open", "open a recording or attach to a session", PaletteGlyph.None, PaletteTarget.None));
            return;
        }

        if (!PaletteQuery.TryParseTime(text, out double ms) || ms < 0)
        {
            rows.Add(PaletteRow.Item(PaletteGroup.Time, "Type a time", "ms from the session start, e.g. :1262.4 or :1.26 s", PaletteGlyph.None,
                PaletteTarget.None));
            return;
        }

        long frequency = Math.Max(1, session.Header.StopwatchFrequency);
        long ticks = session.StartTicks + Core.Model.TimeWindow.TicksFromMilliseconds(ms, frequency);
        rows.Add(PaletteRow.Item(PaletteGroup.Time, "Go to " + ms.ToString("N3", Invariant) + " ms", "moves the playhead and the window",
            PaletteGlyph.Command, PaletteTarget.OfTime(ticks) with { Command = AppCommand.GoToTime }));
    }

    private void Offer(string query, in KeyBuilder key, int id, int bonus = 0)
    {
        int score = FuzzyMatcher.Score(query, key.Span);
        if (score != FuzzyMatcher.NoMatch) _hits.Add(score + bonus, id);
    }

    private KeyBuilder Key() => new(_key, 0);

    private static int Id(int kind, int index) => (kind << 24) | (index & 0xFFFFFF);

    private static KeyBuilder AppendTarget(KeyBuilder key, CaptureSession session, scoped in FrameRecord frame, scoped in FrameLink link, bool withMethod)
    {
        MessageAssembler assembler = session.Assembler;
        string separator = key.Length > 0 ? " " : "";
        if (link.Entity == EntityKind.Invocation && (uint)link.EntityIndex < (uint)assembler.Invocations.Length)
        {
            ref readonly InvocationRow invocation = ref assembler.Invocations[link.EntityIndex];
            key = key.Text(separator).Text("inv ").Number(invocation.InvocationId);
            if (withMethod && invocation.RequestParsed && session.Decoder.TryGetMethod(invocation, out SchemaMethod? method))
                key = key.Text(" ").Text(method.Name);
            return key;
        }

        if (frame.Target == FrameTarget.Invocation) return key.Text(separator).Text("inv ").Number(frame.TargetId);
        if (link.Entity == EntityKind.Pipe && (uint)link.EntityIndex < (uint)assembler.Pipes.Length)
        {
            ref readonly PipeRow pipe = ref assembler.Pipes[link.EntityIndex];
            return key.Text(separator).Text(PipeText.Noun(pipe.Content)).Text(" ").PipeIds(pipe.PipeId);
        }

        return frame.Target == FrameTarget.Pipe ? key.Text(separator).Text("pipe ").PipeIds(frame.TargetId) : key;
    }

    private static string TypeName(in FrameRecord frame) => frame.Kind switch
    {
        FrameKind.Preamble => "Preamble",
        FrameKind.Malformed => "Malformed",
        _ => EventText.TypeName(frame.Type),
    };

    // " 2/5": a chunk's number among its message's chunks, and their count once the message is complete.
    private string ChunkSuffix(MessageAssembler assembler, int message, FrameKey key)
    {
        AssembledMessage assembled = assembler.Messages[message];
        _fragments.Clear();
        assembler.GetFragments(assembled.Ref, _fragments);
        int number = 0, count = 0;
        foreach (TraceFragment fragment in _fragments)
        {
            if (!assembler.TryGetFrame(fragment.Direction, fragment.Seq, out FrameRecord part) || part.Type != MessageType.MessageChunk) continue;
            count++;
            if (fragment.Direction == key.Direction && fragment.Seq == key.Seq) number = count;
        }

        if (number == 0) return "";
        return assembled.State == AssemblyState.Complete
            ? " " + number.ToString(Invariant) + "/" + count.ToString(Invariant)
            : " " + number.ToString(Invariant);
    }

    private static string? OpenerName(CaptureSession session, in PipeRow row) =>
        row.OpenerInvocation >= 0 && row.OpenerInvocation < session.Assembler.Invocations.Length
        && session.Decoder.TryGetMethod(session.Assembler.Invocations[row.OpenerInvocation], out SchemaMethod? method)
            ? method.Name
            : null;

    private static string Lifecycle(in PipeRow row) => row.Lifecycle switch
    {
        PipeLifecycle.Closed => "closed",
        PipeLifecycle.Refused => "refused",
        PipeLifecycle.Closing => "closing",
        _ => "open",
    };

    private static string PipeIds(ushort id) =>
        (id & 0xFF).ToString(Invariant) + "·" + (id >> 8).ToString(Invariant);

    // "7 frames · 1,258.1 – 1,273.4 ms"; past 10 s only the start, in seconds ("2 frames · 31.204 s").
    private static string Span(CaptureSession session, MessageTrace trace)
    {
        if (!trace.IsActive) return "no frames captured";
        long frequency = Math.Max(1, session.Header.StopwatchFrequency);
        double start = (trace.Start - session.StartTicks) * 1000.0 / frequency;
        double end = (trace.End - session.StartTicks) * 1000.0 / frequency;
        string frames = trace.MemberCount.ToString("N0", Invariant) + (trace.MemberCount == 1 ? " frame · " : " frames · ");
        if (start >= 10_000) return frames + (start / 1000).ToString("N3", Invariant) + " s";
        return end - start < 0.05
            ? frames + start.ToString("N1", Invariant) + " ms"
            : frames + start.ToString("N1", Invariant) + " – " + end.ToString("N1", Invariant) + " ms";
    }

    /// <summary>Writes a candidate's search text into the shared buffer; text past its end is dropped.</summary>
    private readonly ref struct KeyBuilder(char[] buffer, int length)
    {
        private readonly char[] _buffer = buffer;

        public int Length { get; } = length;

        public ReadOnlySpan<char> Span => _buffer.AsSpan(0, Length);

        public KeyBuilder Text(ReadOnlySpan<char> text)
        {
            int n = Math.Min(text.Length, _buffer.Length - Length);
            text[..n].CopyTo(_buffer.AsSpan(Length));
            return new KeyBuilder(_buffer, Length + n);
        }

        public KeyBuilder Number(long value) =>
            value.TryFormat(_buffer.AsSpan(Length), out int written, default, CultureInfo.InvariantCulture) ? new KeyBuilder(_buffer, Length + written) : this;

        public KeyBuilder PipeIds(ushort id) => Number(id & 0xFF).Text("·").Number(id >> 8);

        public override string ToString() => new(Span);
    }

    /// <summary>The best hits of one group: scores descending, ties in the order they were offered.</summary>
    private sealed class TopHits(int capacity)
    {
        private readonly int[] _scores = new int[capacity];
        private readonly int[] _ids = new int[capacity];
        private int _limit = capacity;

        public int Count { get; private set; }

        public void Reset(int limit)
        {
            _limit = Math.Clamp(limit, 1, _scores.Length);
            Count = 0;
        }

        public int IdAt(int index) => _ids[index];

        public void Add(int score, int id)
        {
            if (Count == _limit && score <= _scores[Count - 1]) return;
            // After every equal score, so earlier candidates stay first.
            int at = Count == _limit ? Count - 1 : Count;
            while (at > 0 && _scores[at - 1] < score)
            {
                if (at < _limit)
                {
                    _scores[at] = _scores[at - 1];
                    _ids[at] = _ids[at - 1];
                }

                at--;
            }

            _scores[at] = score;
            _ids[at] = id;
            if (Count < _limit) Count++;
        }
    }
}
