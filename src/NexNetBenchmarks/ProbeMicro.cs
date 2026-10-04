#if !NEXNET_MEMORYPACK
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MemoryPack;
using NexNet.Serialization;

namespace NexNetBenchmarks;

// TEMPORARY micro harness (not part of the final change).
public static class ProbeMicro
{
    static double Time(Action a, int n)
    {
        n = Math.Max(1, n / 4);
        for (int i = 0; i < n; i++) a();
        double best = double.MaxValue;
        for (int r = 0; r < 25; r++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) a();
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds / n);
        }
        return best;
    }

    static void Case<T>(string name, T value, int n)
    {
        var f = NexusFormatterRegistry.Get<T>();
        var buf = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buf); f.Serialize(ref w, value); w.Flush();
        var nb = buf.WrittenMemory.ToArray();
        var mb = MemoryPackSerializer.Serialize(value);
        var ser = new ArrayBufferWriter<byte>(1 << 20);
        double ns = Time(() => { var r = new MsgPackReader(nb, NexusSerializerOptions.Untrusted); T? v = default; f.Deserialize(ref r, ref v); }, n);
        double nt = Time(() => { var r = new MsgPackReader(nb, NexusSerializerOptions.Trusted); T? v = default; f.Deserialize(ref r, ref v); }, n);
        double ms = Time(() => MemoryPackSerializer.Deserialize<T>(mb), n);
        double nw = Time(() => { ser.ResetWrittenCount(); var w2 = new MsgPackWriter(ser); f.Serialize(ref w2, value); w2.Flush(); }, n);
        double mw = Time(() => { ser.ResetWrittenCount(); MemoryPackSerializer.Serialize(ser, value); }, n);
        Console.WriteLine($"{name,-14} deser NexNet {ns,8:F1} (trusted {nt,8:F1})  MemPack {ms,8:F1}  ratio {ns / ms,5:F2} | ser NexNet {nw,8:F1} MemPack {mw,8:F1} | S+D {(ns + nw) / (ms + mw),5:F2}");
    }

    static ReadOnlySequence<byte> Split(byte[] data, int seg)
    {
        Seg? first = null, last = null;
        for (int o = 0; o < data.Length; o += seg)
        {
            var s = new Seg(data.AsMemory(o, Math.Min(seg, data.Length - o)));
            if (first == null) first = last = s; else last = last!.Append(s);
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    sealed class Seg : ReadOnlySequenceSegment<byte>
    {
        public Seg(ReadOnlyMemory<byte> m) => Memory = m;
        public Seg Append(Seg n) { n.RunningIndex = RunningIndex + Memory.Length; Next = n; return n; }
    }

    static void Channel(string name, ReadOnlySequence<byte> seq)
    {
        var f = NexusFormatterRegistry.Get<BenchPerson>();
        var list = new List<BenchPerson>(2000);
        double t = Time(() =>
        {
            list.Clear();
            var reader = new MsgPackReader(seq, NexusSerializerOptions.Untrusted);
            while (true)
            {
                var probe = reader;
                if (!probe.TrySkip()) break;
                BenchPerson? item = default;
                f.Deserialize(ref reader, ref item);
                list.Add(item!);
            }
        }, 300);
        double skip = Time(() =>
        {
            var reader = new MsgPackReader(seq, NexusSerializerOptions.Untrusted);
            while (reader.TrySkip()) { }
        }, 300);
        Console.WriteLine($"{name,-24} per item: total {t / 2000,6:F1} ns, TrySkip only {skip / 2000,6:F1} ns");
    }

    public static void Run()
    {
        var proc = Process.GetCurrentProcess();
        proc.PriorityClass = ProcessPriorityClass.High;
        System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;
        var persons = Enumerable.Range(0, 2000).Select(BenchPerson.Create).ToArray();
        {
            var nb = new byte[] { 0xce, 0, 1, 0xe2, 0x40 };
            var small = new byte[] { 5 };
            var f = NexusFormatterRegistry.Get<int>();
            var seq = new ReadOnlySequence<byte>(nb);
            for (int k = 0; k < 2; k++)
            {
                Console.WriteLine($"ctor(byte[])        {Time(() => { var r = new MsgPackReader(nb, NexusSerializerOptions.Untrusted); }, 5_000_000),6:F1}");
                Console.WriteLine($"ctor(seq)           {Time(() => { var r = new MsgPackReader(seq, NexusSerializerOptions.Untrusted); }, 5_000_000),6:F1}");
                Console.WriteLine($"new ROS(byte[])     {Time(() => { var s = new ReadOnlySequence<byte>(nb); }, 5_000_000),6:F1}");
                Console.WriteLine($"empty delegate      {Time(() => { }, 5_000_000),6:F1}");
                Console.WriteLine($"ReadInt32 direct    {Time(() => { var r = new MsgPackReader(nb, NexusSerializerOptions.Untrusted); r.ReadInt32(); }, 5_000_000),6:F1}");
                Console.WriteLine($"ReadInt32 fixint    {Time(() => { var r = new MsgPackReader(small, NexusSerializerOptions.Untrusted); r.ReadInt32(); }, 5_000_000),6:F1}");
                Console.WriteLine($"formatter int32     {Time(() => { var r = new MsgPackReader(nb, NexusSerializerOptions.Untrusted); int v = 0; f.Deserialize(ref r, ref v); }, 5_000_000),6:F1}");
                Console.WriteLine($"NexusSerializer.Des {Time(() => NexusSerializer.Deserialize<int>(nb), 5_000_000),6:F1}");
            }
        }
        if (Environment.GetEnvironmentVariable("MICRO_ONLY_FIXED") == "1") return;
        if (Environment.GetEnvironmentVariable("MICRO_PROFILE_LIST") == "1")
        {
            var lf = NexusFormatterRegistry.Get<List<BenchPerson>>();
            var lb = new ArrayBufferWriter<byte>();
            var lw = new MsgPackWriter(lb); lf.Serialize(ref lw, Enumerable.Range(0, 100).Select(BenchPerson.Create).ToList()); lw.Flush();
            var lbytes = lb.WrittenMemory.ToArray();
            var sw0 = Stopwatch.StartNew();
            while (sw0.Elapsed.TotalSeconds < 15)
                for (int i = 0; i < 1000; i++) { var r = new MsgPackReader(lbytes); List<BenchPerson>? v = null; lf.Deserialize(ref r, ref v); }
            return;
        }
        for (int k = 0; k < 2; k++)
        {
            Case("Int32", 123456, 2_000_000);
            Case("AsciiShort", "hello world", 2_000_000);
            Case("AsciiLong", new string('x', 2000), 200_000);
            Case("Unicode", string.Concat(Enumerable.Repeat("日本語テキスト ü ", 100)), 50_000);
            Case("Poco", BenchPerson.Create(42), 1_000_000);
            Case("NestedGraph", BenchOrder.Create(7), 300_000);
            Case("Doubles16", Enumerable.Range(0, 16).Select(i => i * 0.5).ToArray(), 1_000_000);
            Case("Doubles1K", Enumerable.Range(0, 1024).Select(i => i * 0.5).ToArray(), 200_000);
            Case("PersonList100", Enumerable.Range(0, 100).Select(BenchPerson.Create).ToList(), 20_000);
            Case<IBenchShape>("Union", new BenchSquare { Side = 3.5 }, 1_000_000);

            var buf = new ArrayBufferWriter<byte>();
            var w = new MsgPackWriter(buf);
            foreach (var p in persons) NexusFormatterRegistry.Get<BenchPerson>().Serialize(ref w, p);
            w.Flush();
            var bytes = buf.WrittenMemory.ToArray();
            Channel("channel single-seg", new ReadOnlySequence<byte>(bytes));
            Channel("channel 8K segments", Split(bytes, 8192));
            Channel("channel 1K segments", Split(bytes, 1024));
            Console.WriteLine();
        }
    }
}
#endif
