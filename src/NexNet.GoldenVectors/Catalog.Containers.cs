using NexNet.TestNexus.Interop;

namespace NexNet.GoldenVectors;

public static partial class GoldenGenerator
{
    private static void Containers(GoldenBuilder b)
    {
        b.File("containers");

        // ----- sequences: arrays of anything that is not an ext 78 element
        b.Both<string[]?>("string[]", "seq.string-array.nil", null);
        b.Both("string[]", "seq.string-array.empty", Array.Empty<string>());
        b.Both("string[]", "seq.string-array.2", new[] { "a", "b" });
        b.Both("string[]", "seq.string-array.with-nil", new[] { null, "x" });
        b.Both("List<string>", "seq.string-list", new List<string> { "a", "b" }, "List<T> of a non-primitive is the same array as T[]");
        b.Both<IList<string>>("IList<string>", "seq.string-ilist", new List<string> { "x", "y", "z" });
        b.Both<IReadOnlyList<InteropItem>>("IReadOnlyList<InteropItem>", "seq.item-read-only-list", new List<InteropItem> { new(1, "one"), new(2, null) });
        b.Both("List<int?>", "seq.nullable-int-list", new List<int?> { 1, null, 3 }, "List<int?> is element-wise, not ext 78");
        b.Both("List<string>", "seq.fixarray-15", Enumerable.Range(0, 15).Select(i => "s" + i).ToList(), "longest fixarray");
        b.Both("List<string>", "seq.array16-16", Enumerable.Range(0, 16).Select(i => "s" + i).ToList(), "shortest array16");
        b.Decode<List<string>>("List<string>", "seq.from-array16", "dc0001a161");
        b.Decode<List<string>>("List<string>", "seq.from-array32", "dd00000001a161");
        b.Both("HashSet<string>", "set.string", new HashSet<string> { "a", "b", "c" });
        b.Both("HashSet<int>", "set.int", new HashSet<int> { 3, 1, 2 }, "a set is an array in its enumeration order");
        b.Both("Queue<int>", "queue.int", new Queue<int>([1, 2, 3]), "front first");
        var stack = new Stack<string>();
        stack.Push("a");
        stack.Push("b");
        stack.Push("c");
        b.Both("Stack<string>", "stack.string", stack, "top first: pushed a, b, c");

        b.Reject<string[]>("string[]", "seq.reject.element-type", "9101", "type");
        b.Reject<string[]>("string[]", "seq.reject.map", "80", "type");
        b.Reject<List<string>>("List<string>", "seq.reject.count-length", "dd7fffffff", "length", "the count is checked against the remaining bytes first");
        b.Reject<List<string>>("List<string>", "seq.reject.truncated", "92a161", "truncated");

        // ----- maps: entries in enumeration order
        b.Both("Dictionary<string,int>", "map.string-int", new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 });
        b.Both("Dictionary<string,int>", "map.empty", new Dictionary<string, int>());
        b.Both<Dictionary<string, int>?>("Dictionary<string,int>", "map.nil", null);
        b.Both("Dictionary<string,string>", "map.string-string", new Dictionary<string, string> { ["k"] = "v", ["n"] = null! }, "a null value is nil");
        b.Both("Dictionary<string,string>", "map.fixmap-15", Enumerable.Range(0, 15).ToDictionary(i => "k" + i, i => "v" + i), "longest fixmap");
        b.Both("Dictionary<string,string>", "map.map16-16", Enumerable.Range(0, 16).ToDictionary(i => "k" + i, i => "v" + i), "shortest map16");
        b.Both("Dictionary<int,InteropItem>", "map.int-item", new Dictionary<int, InteropItem> { [-1] = new(-1, "minus"), [7] = new(7, "seven") });
        b.Both("Dictionary<Guid,long>", "map.guid-long", new Dictionary<Guid, long> { [Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")] = long.MinValue });
        b.Both<IDictionary<string, int>>("IDictionary<string,int>", "map.string-int-idictionary", new Dictionary<string, int> { ["x"] = 1 });
        b.Both<IReadOnlyDictionary<int, string>>("IReadOnlyDictionary<int,string>", "map.int-string-read-only", new Dictionary<int, string> { [1] = "one", [2] = "two" });
        b.Both("Dictionary<string,List<int[]>>", "map.nested", new Dictionary<string, List<int[]>> { ["a"] = [[1, 2], []], ["b"] = [] }, "a map of lists of ext 78 arrays");
        b.Decode<Dictionary<string, int>>("Dictionary<string,int>", "map.from-map16", "de0001a16101");
        b.Decode<Dictionary<string, int>>("Dictionary<string,int>", "map.from-map32", "df00000001a16101");
        b.Reject<Dictionary<string, int>>("Dictionary<string,int>", "map.reject.array", "92a16101", "type");
        b.Reject<Dictionary<string, int>>("Dictionary<string,int>", "map.reject.count-length", "df7fffffff", "length");

        // ----- tuples: arrays of exactly their arity
        b.Both("(int,string,double)", "tuple.3", (1, "a", 1.5));
        b.Both("(int,long,string,bool,double,Guid,byte)", "tuple.7", (1, long.MaxValue, "s", true, -0.5, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), (byte)255));
        b.Both("Tuple<int,string>", "tuple.reference", Tuple.Create(5, "five"), "System.Tuple, a reference type");
        b.Both<Tuple<int, string>?>("Tuple<int,string>", "tuple.reference.nil", null);
        b.Both("KeyValuePair<string,int>", "tuple.key-value-pair", new KeyValuePair<string, int>("k", 9), "[key, value]");
        b.Reject<(int, string, double)>("(int,string,double)", "tuple.reject.short", "9201a161", "count");
        b.Reject<(int, string, double)>("(int,string,double)", "tuple.reject.long", "9401a161cb3ff800000000000000", "count");

        // ----- decoding limits (§11)
        var three = new GoldenOptions(MaxElements: 3);
        b.Both("List<string>", "limit.elements.at-limit", new List<string> { "a", "b", "c" }, "3 elements with maxElements 3", three);
        b.Reject<List<string>>("List<string>", "limit.reject.elements", "94a161a162a163a164", "elements", "4 elements with maxElements 3", three);
        b.Reject<Dictionary<string, int>>("Dictionary<string,int>", "limit.reject.elements-map", "82a16101a16202", "elements",
            "map entries count their key and their value: 2 entries are 4 elements", three);
        b.Reject<Dictionary<string, List<int[]>>>("Dictionary<string,List<int[]>>", "limit.reject.elements-cumulative", "81a16192d44e04d44e04", "elements",
            "elements are counted over the whole value: one entry (2) holding a list of 2", three);
    }
}
