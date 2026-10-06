using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

public class TypeHasherTests
{
    [Test]
    public void SimpleType_WithSpecialTypes()
    {
        // Types without [NexusObject] are hashed by name only; their members are not walked
        Run("""
            using System;
            [GenerateStructureHash(ExpectedWalk = "SimpleMessage [NotNexusObject]")]
            class SimpleMessage {
                public int Value1;
                public string Value2;
                public bool Value3;
            }
            """);
    }

    [Test]
    public void NexusObjectType_WithSpecialTypes()
    {
        // SpecialTypes are terminal nodes - they're only shown as member types, not recursively walked
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "SimpleMessage [NexusObject]\n  Value1: Int32 [Key:0]\n  Value2: String [Key:1]\n  Value3: Boolean [Key:2]")]
            [NexusObject]
            partial class SimpleMessage {
                [NexusKey(0)] public int Value1;
                [NexusKey(1)] public string Value2;
                [NexusKey(2)] public bool Value3;
            }
            """);
    }

    [Test]
    public void NullableTypes()
    {
        // Nullable<T> is unwrapped and shown as InnerType?, string? is a SpecialType so not recursively walked
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Value1: Int32? [Key:0]\n  Value2: String? [Key:1]\n    Int32? [Nullable]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int? Value1;
                [NexusKey(1)] public string? Value2;
            }
            """);
    }

    [Test]
    public void ArrayTypes()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Value1: Int32[] [Key:0]\n  Value2: Int32[,] [Key:1]\n  Value3: Int32[,,] [Key:2]\n    Int32[] [Array]\n      Int32 [SpecialType]\n    Int32[,] [Array]\n      Int32 [SpecialType]\n    Int32[,,] [Array]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int[] Value1;
                [NexusKey(1)] public int[,] Value2;
                [NexusKey(2)] public int[,,] Value3;
            }
            """);
    }

    [Test]
    public void NullableArrayTypes()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Value1: Int32?[]? [Key:0]\n  Value2: Int32[]? [Key:1]\n    Int32?[]? [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]\n    Int32[]? [Array]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int?[]? Value1;
                [NexusKey(1)] public int[]? Value2;
            }
            """);
    }

    [Test]
    public void GenericTypes_CLR()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Items: List<1> [Key:0]\n    List<1> [CLR]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<int> Items;
            }
            """);
    }

    [Test]
    public void GenericTypes_WithUserClass()
    {
        // UserData is walked because it's a [NexusObject], but its Int32 member is terminal
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Items: List<1> [Key:0]\n    List<1> [CLR]\n      UserData [NexusObject]\n        Value: Int32 [Key:0]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<UserData> Items;
            }
            [NexusObject]
            partial class UserData {
                [NexusKey(0)] public int Value;
            }
            """);
    }

    [Test]
    public void DictionaryType()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Data: Dictionary<2> [Key:0]\n    Dictionary<2> [CLR]\n      String [SpecialType]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Dictionary<string, int> Data;
            }
            """);
    }

    [Test]
    public void EnumType()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Status: Status [Key:0]\n    Status [Enum]\n      Pending = 0\n      Running = 1\n      Complete = 2\n      Failed = 3")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Status Status;
            }
            enum Status { Pending, Running, Complete, Failed }
            """);
    }

    [Test]
    public void EnumType_WithExplicitValues()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Status: Status [Key:0]\n    Status [Enum]\n      None = 0\n      Warning = 10\n      Error = 100\n      Critical = 500")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Status Status;
            }
            enum Status { None = 0, Warning = 10, Error = 100, Critical = 500 }
            """);
    }

    [Test]
    public void EnumType_WithFlags()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Flags: Permissions [Key:0]\n    Permissions [Enum]\n      None = 0\n      Read = 1\n      Write = 2\n      Execute = 4\n      All = 7")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Permissions Flags;
            }
            [Flags]
            enum Permissions { None = 0, Read = 1, Write = 2, Execute = 4, All = Read | Write | Execute }
            """);
    }

    [Test]
    public void NexusUnion()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "IMessage [NexusUnion:2]\n  [Tag:0] MessageA\n  [Tag:1] MessageB\n    MessageA [NexusObject]\n      Value: Int32 [Key:0]\n    MessageB [NexusObject]\n      Text: String [Key:0]")]
            [NexusObject]
            [NexusUnion<MessageA>(0)]
            [NexusUnion<MessageB>(1)]
            partial interface IMessage { }

            [NexusObject]
            partial class MessageA : IMessage {
                [NexusKey(0)] public int Value;
            }
            [NexusObject]
            partial class MessageB : IMessage {
                [NexusKey(0)] public string Text;
            }
            """);
    }

    [Test]
    public void NexusUnion_SortsByTag()
    {
        // Even though attributes are in reverse order, output is sorted by union tag
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "IMessage [NexusUnion:2]\n  [Tag:0] MessageA\n  [Tag:1] MessageB\n    MessageA [NexusObject]\n      Value: Int32 [Key:0]\n    MessageB [NexusObject]\n      Text: String [Key:0]")]
            [NexusObject]
            [NexusUnion<MessageB>(1)]
            [NexusUnion<MessageA>(0)]
            partial interface IMessage { }

            [NexusObject]
            partial class MessageA : IMessage {
                [NexusKey(0)] public int Value;
            }
            [NexusObject]
            partial class MessageB : IMessage {
                [NexusKey(0)] public string Text;
            }
            """);
    }

    [Test]
    public void NexusKey_MembersSortedByKey()
    {
        // Members are sorted by NexusKey, not declaration order; SpecialTypes are terminal
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  First: Int16 [Key:0]\n  Second: Int32 [Key:1]\n  Third: Int64 [Key:2]")]
            [NexusObject]
            partial class Message {
                [NexusKey(2)] public long Third;
                [NexusKey(1)] public int Second;
                [NexusKey(0)] public short First;
            }
            """);
    }

    [Test]
    public void CyclicReference_SelfReferencing()
    {
        // Self-referencing type shows [seen] on second encounter
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Node [NexusObject]\n  Value: Int32 [Key:0]\n  Next: Node? [Key:1]\n    Node [seen]")]
            [NexusObject]
            partial class Node {
                [NexusKey(0)] public int Value;
                [NexusKey(1)] public Node? Next;
            }
            """);
    }

    [Test]
    public void CyclicReference_MutualReference()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "NodeA [NexusObject]\n  Value: Int32 [Key:0]\n  Other: NodeB? [Key:1]\n    NodeB? [NexusObject]\n      Value: String [Key:0]\n      Other: NodeA? [Key:1]\n        NodeA [seen]")]
            [NexusObject]
            partial class NodeA {
                [NexusKey(0)] public int Value;
                [NexusKey(1)] public NodeB? Other;
            }
            [NexusObject]
            partial class NodeB {
                [NexusKey(0)] public string Value;
                [NexusKey(1)] public NodeA? Other;
            }
            """);
    }

    [Test]
    public void NonNexusObject_UserType()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Data: NonSerializable [Key:0]\n    NonSerializable [NotNexusObject]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public NonSerializable Data;
            }
            class NonSerializable {
                public int Value1;
                public string Value2;
            }
            """);
    }

    [Test]
    public void CLRType_SystemNamespace()
    {
        // DateTime is a SpecialType in Roslyn (terminal, not walked)
        // Guid and TimeSpan are CLR types (walked, shown as [CLR])
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Time: DateTime [Key:0]\n  Id: Guid [Key:1]\n  Span: TimeSpan [Key:2]\n    Guid [CLR]\n    TimeSpan [CLR]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public DateTime Time;
                [NexusKey(1)] public Guid Id;
                [NexusKey(2)] public TimeSpan Span;
            }
            """);
    }

    [Test]
    public void NestedTypes()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Outer [NexusObject]\n  Inner: InnerType [Key:0]\n    InnerType [NexusObject]\n      Value: Int32 [Key:0]")]
            [NexusObject]
            partial class Outer {
                [NexusKey(0)] public InnerType Inner;
            }
            [NexusObject]
            partial class InnerType {
                [NexusKey(0)] public int Value;
            }
            """);
    }

    [Test]
    public void ComplexNestedGenerics()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Data: Dictionary<2> [Key:0]\n    Dictionary<2> [CLR]\n      String [SpecialType]\n      List<1> [CLR]\n        UserData [NexusObject]\n          Id: Int32 [Key:0]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Dictionary<string, List<UserData>> Data;
            }
            [NexusObject]
            partial class UserData {
                [NexusKey(0)] public int Id;
            }
            """);
    }

    [Test]
    public void IgnoresPrivateMembers()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  PublicValue: Int32 [Key:0]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int PublicValue;
                private int PrivateValue;
                protected int ProtectedValue;
                internal int InternalValue;
            }
            """);
    }

    [Test]
    public void IgnoresStaticMembers()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  InstanceValue: Int32 [Key:0]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int InstanceValue;
                public static int StaticValue;
            }
            """);
    }

    [Test]
    public void StructType()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  X: Int32 [Key:0]\n  Y: Int32 [Key:1]")]
            [NexusObject]
            partial struct Message {
                [NexusKey(0)] public int X;
                [NexusKey(1)] public int Y;
            }
            """);
    }

    #region Deep Nested and Complex Type Tests

    [Test]
    public void DeepNested_ThreeLevels()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Level1 [NexusObject]\n  Value: Int32 [Key:0]\n  Child: Level2 [Key:1]\n    Level2 [NexusObject]\n      Value: String [Key:0]\n      Child: Level3 [Key:1]\n        Level3 [NexusObject]\n          Value: Boolean [Key:0]")]
            [NexusObject]
            partial class Level1 {
                [NexusKey(0)] public int Value;
                [NexusKey(1)] public Level2 Child;
            }
            [NexusObject]
            partial class Level2 {
                [NexusKey(0)] public string Value;
                [NexusKey(1)] public Level3 Child;
            }
            [NexusObject]
            partial class Level3 {
                [NexusKey(0)] public bool Value;
            }
            """);
    }

    [Test]
    public void DeepNested_FiveLevels_WithNullables()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Root [NexusObject]\n  Id: Int32 [Key:0]\n  A: LevelA? [Key:1]\n    LevelA? [NexusObject]\n      Name: String [Key:0]\n      B: LevelB? [Key:1]\n        LevelB? [NexusObject]\n          Count: Int64 [Key:0]\n          C: LevelC? [Key:1]\n            LevelC? [NexusObject]\n              Flag: Boolean [Key:0]\n              D: LevelD? [Key:1]\n                LevelD? [NexusObject]\n                  Data: Double [Key:0]")]
            [NexusObject]
            partial class Root {
                [NexusKey(0)] public int Id;
                [NexusKey(1)] public LevelA? A;
            }
            [NexusObject]
            partial class LevelA {
                [NexusKey(0)] public string Name;
                [NexusKey(1)] public LevelB? B;
            }
            [NexusObject]
            partial class LevelB {
                [NexusKey(0)] public long Count;
                [NexusKey(1)] public LevelC? C;
            }
            [NexusObject]
            partial class LevelC {
                [NexusKey(0)] public bool Flag;
                [NexusKey(1)] public LevelD? D;
            }
            [NexusObject]
            partial class LevelD {
                [NexusKey(0)] public double Data;
            }
            """);
    }

    [Test]
    public void SelfReferencing_LinkedList()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "LinkedNode [NexusObject]\n  Value: Int32 [Key:0]\n  Next: LinkedNode? [Key:1]\n  Prev: LinkedNode? [Key:2]\n    LinkedNode [seen]\n    LinkedNode [seen]")]
            [NexusObject]
            partial class LinkedNode {
                [NexusKey(0)] public int Value;
                [NexusKey(1)] public LinkedNode? Next;
                [NexusKey(2)] public LinkedNode? Prev;
            }
            """);
    }

    [Test]
    public void SelfReferencing_TreeStructure()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "TreeNode [NexusObject]\n  Id: Int32 [Key:0]\n  Name: String [Key:1]\n  Parent: TreeNode? [Key:2]\n  Children: List<1>? [Key:3]\n    TreeNode [seen]\n    List<1>? [CLR]\n      TreeNode [seen]")]
            [NexusObject]
            partial class TreeNode {
                [NexusKey(0)] public int Id;
                [NexusKey(1)] public string Name;
                [NexusKey(2)] public TreeNode? Parent;
                [NexusKey(3)] public List<TreeNode>? Children;
            }
            """);
    }

    [Test]
    public void SelfReferencing_DeepChain_WithArrays()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "ChainNode [NexusObject]\n  Data: Int32 [Key:0]\n  Next: ChainNode? [Key:1]\n  Siblings: ChainNode[]? [Key:2]\n    ChainNode [seen]\n    ChainNode[]? [Array]\n      ChainNode [seen]")]
            [NexusObject]
            partial class ChainNode {
                [NexusKey(0)] public int Data;
                [NexusKey(1)] public ChainNode? Next;
                [NexusKey(2)] public ChainNode[]? Siblings;
            }
            """);
    }

    #endregion

    #region Arity Tests

    [Test]
    public void Arity_SingleGeneric()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Items: List<1> [Key:0]\n    List<1> [CLR]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<int> Items;
            }
            """);
    }

    [Test]
    public void Arity_DoubleGeneric()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Map: Dictionary<2> [Key:0]\n    Dictionary<2> [CLR]\n      String [SpecialType]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Dictionary<string, int> Map;
            }
            """);
    }

    [Test]
    public void Arity_TripleGeneric()
    {
        // Generic types always walk their type arguments, even if the generic itself is not a [NexusObject]
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Data: MyTriple<3> [Key:0]\n    MyTriple<3>\n      Int32 [SpecialType]\n      String [SpecialType]\n      Boolean [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public MyTriple<int, string, bool> Data;
            }
            class MyTriple<T1, T2, T3> {
                public T1 First;
                public T2 Second;
                public T3 Third;
            }
            """);
    }

    [Test]
    public void Arity_DifferentArities_DifferentHashes()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "MessageWithList [NexusObject]\n  Data: List<1> [Key:0]\n    List<1> [CLR]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class MessageWithList {
                [NexusKey(0)] public List<int> Data;
            }
            """);

        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "MessageWithDict [NexusObject]\n  Data: Dictionary<2> [Key:0]\n    Dictionary<2> [CLR]\n      Int32 [SpecialType]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class MessageWithDict {
                [NexusKey(0)] public Dictionary<int, int> Data;
            }
            """);
    }

    [Test]
    public void Arity_NestedGenerics_DifferentArities()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Level1: List<1> [Key:0]\n  Level2: Dictionary<2> [Key:1]\n    List<1> [CLR]\n      Dictionary<2> [CLR]\n        String [SpecialType]\n        Int32 [SpecialType]\n    Dictionary<2> [CLR]\n      String [SpecialType]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<Dictionary<string, int>> Level1;
                [NexusKey(1)] public Dictionary<string, int> Level2;
            }
            """);
    }

    [Test]
    public void Arity_GenericWithNexusObject()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Items: List<1> [Key:0]\n  Map: Dictionary<2> [Key:1]\n    List<1> [CLR]\n      Inner [NexusObject]\n        Value: Int32 [Key:0]\n    Dictionary<2> [CLR]\n      String [SpecialType]\n      Inner [seen]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<Inner> Items;
                [NexusKey(1)] public Dictionary<string, Inner> Map;
            }
            [NexusObject]
            partial class Inner {
                [NexusKey(0)] public int Value;
            }
            """);
    }

    #endregion

    #region Array Variations

    [Test]
    public void Array_SingleDimension()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Data: Int32[] [Key:0]\n    Int32[] [Array]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int[] Data;
            }
            """);
    }

    [Test]
    public void Array_MultiDimension_AllRanks()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Rank1: Int32[] [Key:0]\n  Rank2: Int32[,] [Key:1]\n  Rank3: Int32[,,] [Key:2]\n  Rank4: Int32[,,,] [Key:3]\n    Int32[] [Array]\n      Int32 [SpecialType]\n    Int32[,] [Array]\n      Int32 [SpecialType]\n    Int32[,,] [Array]\n      Int32 [SpecialType]\n    Int32[,,,] [Array]\n      Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int[] Rank1;
                [NexusKey(1)] public int[,] Rank2;
                [NexusKey(2)] public int[,,] Rank3;
                [NexusKey(3)] public int[,,,] Rank4;
            }
            """);
    }

    [Test]
    public void Array_OfNexusObject()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Items: Item[] [Key:0]\n    Item[] [Array]\n      Item [NexusObject]\n        Id: Int32 [Key:0]\n        Name: String [Key:1]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Item[] Items;
            }
            [NexusObject]
            partial class Item {
                [NexusKey(0)] public int Id;
                [NexusKey(1)] public string Name;
            }
            """);
    }

    [Test]
    public void Array_OfArrays_JaggedArrays()
    {
        // Jagged arrays: the outer array type displays without full element type in the walk string
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Jagged: [] [Key:0]\n    [] [Array]\n      Int32[] [Array]\n        Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int[][] Jagged;
            }
            """);
    }

    [Test]
    public void Array_OfNullableElements()
    {
        // string? in an array shows the nullability annotation
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  NullableInts: Int32?[] [Key:0]\n  NullableStrings: String?[] [Key:1]\n    Int32?[] [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]\n    String?[] [Array]\n      String? [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int?[] NullableInts;
                [NexusKey(1)] public string?[] NullableStrings;
            }
            """);
    }

    [Test]
    public void Array_NullableArray_OfNullableElements()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Data: Int32?[]? [Key:0]\n    Int32?[]? [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int?[]? Data;
            }
            """);
    }

    [Test]
    public void Array_MultiDim_Nullable()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Matrix: Int32?[,]? [Key:0]\n    Int32?[,]? [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int?[,]? Matrix;
            }
            """);
    }

    #endregion

    #region Nullable Variations

    [Test]
    public void Nullable_PrimitiveTypes()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  NInt: Int32? [Key:0]\n  NLong: Int64? [Key:1]\n  NBool: Boolean? [Key:2]\n  NDouble: Double? [Key:3]\n  NDecimal: Decimal? [Key:4]\n    Int32? [Nullable]\n      Int32 [SpecialType]\n    Int64? [Nullable]\n      Int64 [SpecialType]\n    Boolean? [Nullable]\n      Boolean [SpecialType]\n    Double? [Nullable]\n      Double [SpecialType]\n    Decimal? [Nullable]\n      Decimal [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int? NInt;
                [NexusKey(1)] public long? NLong;
                [NexusKey(2)] public bool? NBool;
                [NexusKey(3)] public double? NDouble;
                [NexusKey(4)] public decimal? NDecimal;
            }
            """);
    }

    [Test]
    public void Nullable_ReferenceTypes()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  NullableString: String? [Key:0]\n  NullableObject: Object? [Key:1]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public string? NullableString;
                [NexusKey(1)] public object? NullableObject;
            }
            """);
    }

    [Test]
    public void Nullable_NexusObjectTypes()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Item: InnerItem? [Key:0]\n    InnerItem? [NexusObject]\n      Value: Int32 [Key:0]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public InnerItem? Item;
            }
            [NexusObject]
            partial class InnerItem {
                [NexusKey(0)] public int Value;
            }
            """);
    }

    [Test]
    public void Nullable_InGenerics()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  NullableList: List<1>? [Key:0]\n  ListOfNullable: List<1> [Key:1]\n    List<1>? [CLR]\n      Int32 [SpecialType]\n    List<1> [CLR]\n      Int32? [Nullable]\n        Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<int>? NullableList;
                [NexusKey(1)] public List<int?> ListOfNullable;
            }
            """);
    }

    [Test]
    public void Nullable_DictionaryVariations()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Dict1: Dictionary<2>? [Key:0]\n  Dict2: Dictionary<2> [Key:1]\n    Dictionary<2>? [CLR]\n      String [SpecialType]\n      Int32 [SpecialType]\n    Dictionary<2> [CLR]\n      String? [SpecialType]\n      Int32? [Nullable]\n        Int32 [SpecialType]")]
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Dictionary<string, int>? Dict1;
                [NexusKey(1)] public Dictionary<string?, int?> Dict2;
            }
            """);
    }

    #endregion

    #region Complex Deep Walk Scenarios

    [Test]
    public void ComplexDeepWalk_TreeWithCollections()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Organization [NexusObject]\n  Id: Int32 [Key:0]\n  Name: String [Key:1]\n  Root: Department? [Key:2]\n    Department? [NexusObject]\n      Id: Int32 [Key:0]\n      Name: String [Key:1]\n      Parent: Department? [Key:2]\n      SubDepartments: List<1>? [Key:3]\n      Employees: Employee[]? [Key:4]\n        Department [seen]\n        List<1>? [CLR]\n          Department [seen]\n        Employee[]? [Array]\n          Employee [NexusObject]\n            Id: Int32 [Key:0]\n            Name: String [Key:1]\n            Manager: Employee? [Key:2]\n              Employee [seen]")]
            [NexusObject]
            partial class Organization {
                [NexusKey(0)] public int Id;
                [NexusKey(1)] public string Name;
                [NexusKey(2)] public Department? Root;
            }
            [NexusObject]
            partial class Department {
                [NexusKey(0)] public int Id;
                [NexusKey(1)] public string Name;
                [NexusKey(2)] public Department? Parent;
                [NexusKey(3)] public List<Department>? SubDepartments;
                [NexusKey(4)] public Employee[]? Employees;
            }
            [NexusObject]
            partial class Employee {
                [NexusKey(0)] public int Id;
                [NexusKey(1)] public string Name;
                [NexusKey(2)] public Employee? Manager;
            }
            """);
    }

    [Test]
    public void ComplexDeepWalk_GraphWithAllTypes()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Graph [NexusObject]\n  Id: Guid [Key:0]\n  Name: String? [Key:1]\n  Metadata: Dictionary<2>? [Key:2]\n  Nodes: GraphNode[]? [Key:3]\n    Guid [CLR]\n    Dictionary<2>? [CLR]\n      String [SpecialType]\n      Object [SpecialType]\n    GraphNode[]? [Array]\n      GraphNode [NexusObject]\n        Id: Int32 [Key:0]\n        Label: String? [Key:1]\n        Weight: Double? [Key:2]\n        Tags: String[]? [Key:3]\n        Edges: List<1>? [Key:4]\n        Data: NodeData? [Key:5]\n          Double? [Nullable]\n            Double [SpecialType]\n          String[]? [Array]\n            String [SpecialType]\n          List<1>? [CLR]\n            Edge [NexusObject]\n              Source: GraphNode? [Key:0]\n              Target: GraphNode? [Key:1]\n              Weight: Decimal? [Key:2]\n                GraphNode [seen]\n                GraphNode [seen]\n                Decimal? [Nullable]\n                  Decimal [SpecialType]\n          NodeData? [NexusObject]\n            Values: Int32[]? [Key:0]\n            Matrix: Double[,]? [Key:1]\n            Flags: NodeFlags? [Key:2]\n              Int32[]? [Array]\n                Int32 [SpecialType]\n              Double[,]? [Array]\n                Double [SpecialType]\n              NodeFlags? [Nullable]\n                NodeFlags [Enum]\n                  None = 0\n                  Active = 1\n                  Visited = 2\n                  Processed = 4")]
            [NexusObject]
            partial class Graph {
                [NexusKey(0)] public Guid Id;
                [NexusKey(1)] public string? Name;
                [NexusKey(2)] public Dictionary<string, object>? Metadata;
                [NexusKey(3)] public GraphNode[]? Nodes;
            }
            [NexusObject]
            partial class GraphNode {
                [NexusKey(0)] public int Id;
                [NexusKey(1)] public string? Label;
                [NexusKey(2)] public double? Weight;
                [NexusKey(3)] public string[]? Tags;
                [NexusKey(4)] public List<Edge>? Edges;
                [NexusKey(5)] public NodeData? Data;
            }
            [NexusObject]
            partial class Edge {
                [NexusKey(0)] public GraphNode? Source;
                [NexusKey(1)] public GraphNode? Target;
                [NexusKey(2)] public decimal? Weight;
            }
            [NexusObject]
            partial class NodeData {
                [NexusKey(0)] public int[]? Values;
                [NexusKey(1)] public double[,]? Matrix;
                [NexusKey(2)] public NodeFlags? Flags;
            }
            [Flags]
            enum NodeFlags { None = 0, Active = 1, Visited = 2, Processed = 4 }
            """);
    }

    [Test]
    public void ComplexDeepWalk_UnionWithDeepTypes()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "IEvent [NexusUnion:3]\n  [Tag:0] UserEvent\n  [Tag:1] SystemEvent\n  [Tag:2] DataEvent\n    UserEvent [NexusObject]\n      UserId: Int32 [Key:0]\n      Action: String [Key:1]\n      Timestamp: DateTime [Key:2]\n      Metadata: Dictionary<2>? [Key:3]\n        Dictionary<2>? [CLR]\n          String [SpecialType]\n          String [SpecialType]\n    SystemEvent [NexusObject]\n      Level: LogLevel [Key:0]\n      Message: String [Key:1]\n      Source: String? [Key:2]\n      StackTrace: String[]? [Key:3]\n        LogLevel [Enum]\n          Debug = 0\n          Info = 1\n          Warning = 2\n          Error = 3\n        String[]? [Array]\n          String [SpecialType]\n    DataEvent [NexusObject]\n      EntityId: Guid [Key:0]\n      Changes: FieldChange[]? [Key:1]\n        Guid [CLR]\n        FieldChange[]? [Array]\n          FieldChange [NexusObject]\n            FieldName: String [Key:0]\n            OldValue: Object? [Key:1]\n            NewValue: Object? [Key:2]")]
            [NexusObject]
            [NexusUnion<UserEvent>(0)]
            [NexusUnion<SystemEvent>(1)]
            [NexusUnion<DataEvent>(2)]
            partial interface IEvent { }

            [NexusObject]
            partial class UserEvent : IEvent {
                [NexusKey(0)] public int UserId;
                [NexusKey(1)] public string Action;
                [NexusKey(2)] public DateTime Timestamp;
                [NexusKey(3)] public Dictionary<string, string>? Metadata;
            }
            [NexusObject]
            partial class SystemEvent : IEvent {
                [NexusKey(0)] public LogLevel Level;
                [NexusKey(1)] public string Message;
                [NexusKey(2)] public string? Source;
                [NexusKey(3)] public string[]? StackTrace;
            }
            enum LogLevel { Debug, Info, Warning, Error }
            [NexusObject]
            partial class DataEvent : IEvent {
                [NexusKey(0)] public Guid EntityId;
                [NexusKey(1)] public FieldChange[]? Changes;
            }
            [NexusObject]
            partial class FieldChange {
                [NexusKey(0)] public string FieldName;
                [NexusKey(1)] public object? OldValue;
                [NexusKey(2)] public object? NewValue;
            }
            """);
    }

    [Test]
    public void ComplexDeepWalk_AllNullableVariants()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "NullableShowcase [NexusObject]\n  NullableInt: Int32? [Key:0]\n  NullableGuid: Guid? [Key:1]\n  NullableEnum: Status? [Key:2]\n  NullableArray: Int32[]? [Key:3]\n  NullableArrayOfNullable: Int32?[]? [Key:4]\n  NullableList: List<1>? [Key:5]\n  NullableDict: Dictionary<2>? [Key:6]\n  NullableNested: NestedNullable? [Key:7]\n    Int32? [Nullable]\n      Int32 [SpecialType]\n    Guid? [Nullable]\n      Guid [CLR]\n    Status? [Nullable]\n      Status [Enum]\n        Pending = 0\n        Active = 1\n        Complete = 2\n    Int32[]? [Array]\n      Int32 [SpecialType]\n    Int32?[]? [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]\n    List<1>? [CLR]\n      String? [SpecialType]\n    Dictionary<2>? [CLR]\n      String [SpecialType]\n      Inner? [NexusObject]\n        Value: Int32? [Key:0]\n          Int32? [Nullable]\n            Int32 [SpecialType]\n    NestedNullable? [NexusObject]\n      Deep: DeepNullable? [Key:0]\n        DeepNullable? [NexusObject]\n          Values: Int32?[]? [Key:0]\n            Int32?[]? [Array]\n              Int32? [Nullable]\n                Int32 [SpecialType]")]
            [NexusObject]
            partial class NullableShowcase {
                [NexusKey(0)] public int? NullableInt;
                [NexusKey(1)] public Guid? NullableGuid;
                [NexusKey(2)] public Status? NullableEnum;
                [NexusKey(3)] public int[]? NullableArray;
                [NexusKey(4)] public int?[]? NullableArrayOfNullable;
                [NexusKey(5)] public List<string?>? NullableList;
                [NexusKey(6)] public Dictionary<string, Inner?>? NullableDict;
                [NexusKey(7)] public NestedNullable? NullableNested;
            }
            enum Status { Pending, Active, Complete }
            [NexusObject]
            partial class Inner {
                [NexusKey(0)] public int? Value;
            }
            [NexusObject]
            partial class NestedNullable {
                [NexusKey(0)] public DeepNullable? Deep;
            }
            [NexusObject]
            partial class DeepNullable {
                [NexusKey(0)] public int?[]? Values;
            }
            """);
    }

    [Test]
    public void ComplexDeepWalk_SelfReferencingTree_WithAllFeatures()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Document [NexusObject]\n  Id: Guid [Key:0]\n  Title: String [Key:1]\n  Root: Section? [Key:2]\n    Guid [CLR]\n    Section? [NexusObject]\n      Id: Int32 [Key:0]\n      Title: String [Key:1]\n      Content: String? [Key:2]\n      Parent: Section? [Key:3]\n      Children: Section[]? [Key:4]\n      Annotations: Dictionary<2>? [Key:5]\n      Tags: List<1>? [Key:6]\n      Metadata: SectionMeta? [Key:7]\n        Section [seen]\n        Section[]? [Array]\n          Section [seen]\n        Dictionary<2>? [CLR]\n          String [SpecialType]\n          Annotation [NexusObject]\n            Type: AnnotationType [Key:0]\n            Text: String [Key:1]\n            Range: TextRange? [Key:2]\n              AnnotationType [Enum]\n                Comment = 0\n                Highlight = 1\n                Bookmark = 2\n              TextRange? [NexusObject]\n                Start: Int32 [Key:0]\n                End: Int32? [Key:1]\n                  Int32? [Nullable]\n                    Int32 [SpecialType]\n        List<1>? [CLR]\n          String [SpecialType]\n        SectionMeta? [NexusObject]\n          CreatedAt: DateTime [Key:0]\n          ModifiedAt: DateTime? [Key:1]\n          Author: String? [Key:2]\n          Flags: SectionFlags? [Key:3]\n            DateTime? [Nullable]\n              DateTime [SpecialType]\n            SectionFlags? [Nullable]\n              SectionFlags [Enum]\n                None = 0\n                Draft = 1\n                Published = 2\n                Archived = 4")]
            [NexusObject]
            partial class Document {
                [NexusKey(0)] public Guid Id;
                [NexusKey(1)] public string Title;
                [NexusKey(2)] public Section? Root;
            }
            [NexusObject]
            partial class Section {
                [NexusKey(0)] public int Id;
                [NexusKey(1)] public string Title;
                [NexusKey(2)] public string? Content;
                [NexusKey(3)] public Section? Parent;
                [NexusKey(4)] public Section[]? Children;
                [NexusKey(5)] public Dictionary<string, Annotation>? Annotations;
                [NexusKey(6)] public List<string>? Tags;
                [NexusKey(7)] public SectionMeta? Metadata;
            }
            [NexusObject]
            partial class Annotation {
                [NexusKey(0)] public AnnotationType Type;
                [NexusKey(1)] public string Text;
                [NexusKey(2)] public TextRange? Range;
            }
            enum AnnotationType { Comment, Highlight, Bookmark }
            [NexusObject]
            partial class TextRange {
                [NexusKey(0)] public int Start;
                [NexusKey(1)] public int? End;
            }
            [NexusObject]
            partial class SectionMeta {
                [NexusKey(0)] public DateTime CreatedAt;
                [NexusKey(1)] public DateTime? ModifiedAt;
                [NexusKey(2)] public string? Author;
                [NexusKey(3)] public SectionFlags? Flags;
            }
            [Flags]
            enum SectionFlags { None = 0, Draft = 1, Published = 2, Archived = 4 }
            """);
    }

    #endregion

    #region Built-in Types

    [Test]
    public void NullableOfT_AndQuestionMark_WalkTheSame()
    {
        // Nullable<int> and int? are the same type; both spellings produce the same member lines
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Value1: Int32?[] [Key:0]\n  Value2: Int32?[] [Key:1]\n  Value3: Int32? [Key:2]\n  Value4: Int32? [Key:3]\n    Int32?[] [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]\n    Int32?[] [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]\n    Int32? [Nullable]\n      Int32 [SpecialType]\n    Int32? [Nullable]\n      Int32 [SpecialType]")]
            [NexusObject]
            class Message {
                [NexusKey(0)] public Nullable<int>[] Value1;
                [NexusKey(1)] public int?[] Value2;
                [NexusKey(2)] public Nullable<int> Value3;
                [NexusKey(3)] public int? Value4;
            }
            """);
    }

    [Test]
    public void Tuples_WalkTypeArguments()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Value1: ValueTuple<1> [Key:0]\n  Value2: ValueTuple<1> [Key:1]\n  Value3: Tuple<2> [Key:2]\n  Value4: Tuple<1> [Key:3]\n    ValueTuple<1> [CLR]\n      Int32 [SpecialType]\n    ValueTuple<1> [CLR]\n      Tuple<1> [CLR]\n        Int32 [SpecialType]\n    Tuple<2> [CLR]\n      Int32 [SpecialType]\n      String [SpecialType]\n    Tuple<1> [CLR]\n      ValueTuple<2> [CLR]\n        Int32 [SpecialType]\n        Int64 [SpecialType]")]
            [NexusObject]
            class Message {
                [NexusKey(0)] public ValueTuple<int> Value1;
                [NexusKey(1)] public ValueTuple<Tuple<int>> Value2;
                [NexusKey(2)] public Tuple<int, string> Value3;
                [NexusKey(3)] public Tuple<ValueTuple<int, long>> Value4;
            }
            """);
    }

    [Test]
    public void NestedCollectionsAndTuples()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Value1: List<1> [Key:0]\n    List<1> [CLR]\n      ValueTuple<4> [CLR]\n        List<1> [CLR]\n          Dictionary<2> [CLR]\n            Byte [SpecialType]\n            Int32 [SpecialType]\n        String? [SpecialType]\n        Int32[] [Array]\n          Int32 [SpecialType]\n        Int32[]? [Array]\n          Int32 [SpecialType]")]
            [NexusObject]
            class Message {
                [NexusKey(0)] public List<ValueTuple<List<Dictionary<byte, int>>, string?, int[], int[]?>> Value1;
            }
            """);
    }

    [Test]
    public void BuiltInLeafTypes()
    {
        // Types with built-in formatters are hashed by name; System types are not walked
        Run("""
            using System;
            using System.Numerics;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Value1: String [Key:0]\n  Value2: Decimal [Key:1]\n  Value3: DateTime [Key:2]\n  Value4: DateTimeOffset [Key:3]\n  Value5: TimeSpan [Key:4]\n  Value6: DateOnly [Key:5]\n  Value7: TimeOnly [Key:6]\n  Value8: Guid [Key:7]\n  Value9: Half [Key:8]\n  Value10: BigInteger [Key:9]\n  Value11: Uri [Key:10]\n  Value12: Version [Key:11]\n  Value13: Byte[] [Key:12]\n    DateTimeOffset [CLR]\n    TimeSpan [CLR]\n    DateOnly [CLR]\n    TimeOnly [CLR]\n    Guid [CLR]\n    Half [CLR]\n    BigInteger [CLR]\n    Uri [CLR]\n    Version [CLR]\n    Byte[] [Array]\n      Byte [SpecialType]")]
            [NexusObject]
            class Message {
                [NexusKey(0)] public string Value1;
                [NexusKey(1)] public decimal Value2;
                [NexusKey(2)] public DateTime Value3;
                [NexusKey(3)] public DateTimeOffset Value4;
                [NexusKey(4)] public TimeSpan Value5;
                [NexusKey(5)] public DateOnly Value6;
                [NexusKey(6)] public TimeOnly Value7;
                [NexusKey(7)] public Guid Value8;
                [NexusKey(8)] public Half Value9;
                [NexusKey(9)] public BigInteger Value10;
                [NexusKey(10)] public Uri Value11;
                [NexusKey(11)] public Version Value12;
                [NexusKey(12)] public byte[] Value13;
            }
            """);
    }

    [Test]
    public void BuiltInGenericTypes()
    {
        Run("""
            using System;
            using System.Buffers;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Value1: Memory<1> [Key:0]\n  Value2: ReadOnlyMemory<1> [Key:1]\n  Value3: ArraySegment<1> [Key:2]\n  Value4: ReadOnlySequence<1> [Key:3]\n  Value5: KeyValuePair<2> [Key:4]\n  Value6: HashSet<1> [Key:5]\n  Value7: Queue<1> [Key:6]\n  Value8: Stack<1> [Key:7]\n  Value9: IList<1> [Key:8]\n  Value10: IReadOnlyList<1> [Key:9]\n  Value11: ICollection<1> [Key:10]\n  Value12: IReadOnlyCollection<1> [Key:11]\n  Value13: IEnumerable<1> [Key:12]\n  Value14: IDictionary<2> [Key:13]\n  Value15: IReadOnlyDictionary<2> [Key:14]\n    Memory<1> [CLR]\n      Byte [SpecialType]\n    ReadOnlyMemory<1> [CLR]\n      Int32 [SpecialType]\n    ArraySegment<1> [CLR]\n      Byte [SpecialType]\n    ReadOnlySequence<1> [CLR]\n      Byte [SpecialType]\n    KeyValuePair<2> [CLR]\n      Byte [SpecialType]\n      Int32 [SpecialType]\n    HashSet<1> [CLR]\n      Int32 [SpecialType]\n    Queue<1> [CLR]\n      Int32 [SpecialType]\n    Stack<1> [CLR]\n      Int32 [SpecialType]\n    IList<1> [CLR]\n      Int32 [SpecialType]\n    IReadOnlyList<1> [CLR]\n      Int32 [SpecialType]\n    ICollection<1> [CLR]\n      Int32 [SpecialType]\n    IReadOnlyCollection<1> [CLR]\n      Int32 [SpecialType]\n    IEnumerable<1> [CLR]\n      Int32 [SpecialType]\n    IDictionary<2> [CLR]\n      Int32 [SpecialType]\n      Int64 [SpecialType]\n    IReadOnlyDictionary<2> [CLR]\n      Int32 [SpecialType]\n      Int64 [SpecialType]")]
            [NexusObject]
            class Message {
                [NexusKey(0)] public Memory<byte> Value1;
                [NexusKey(1)] public ReadOnlyMemory<int> Value2;
                [NexusKey(2)] public ArraySegment<byte> Value3;
                [NexusKey(3)] public ReadOnlySequence<byte> Value4;
                [NexusKey(4)] public KeyValuePair<byte, int> Value5;
                [NexusKey(5)] public HashSet<int> Value6;
                [NexusKey(6)] public Queue<int> Value7;
                [NexusKey(7)] public Stack<int> Value8;
                [NexusKey(8)] public IList<int> Value9;
                [NexusKey(9)] public IReadOnlyList<int> Value10;
                [NexusKey(10)] public ICollection<int> Value11;
                [NexusKey(11)] public IReadOnlyCollection<int> Value12;
                [NexusKey(12)] public IEnumerable<int> Value13;
                [NexusKey(13)] public IDictionary<int, long> Value14;
                [NexusKey(14)] public IReadOnlyDictionary<int, long> Value15;
            }
            """);
    }

    [Test]
    public void Enum_MembersSortedByValue()
    {
        // Enum members are walked in value order, not declaration order
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Value1: Status [Key:0]\n    Status [Enum]\n      Unset = 0\n      EOF = 1\n      Running = 10\n      Stopped = 20\n      Error = 100")]
            [NexusObject]
            class Message {
                [NexusKey(0)] public Status Value1;
            }
            enum Status { Unset = 0, Running = 10, Stopped = 20, Error = 100, EOF = 1 }
            """);
    }

    [Test]
    public void Enum_FlagsWithCombinedValues()
    {
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Message [NexusObject]\n  Value1: Status [Key:0]\n    Status [Enum]\n      Unset = 0\n      Running = 1\n      Stopped = 4\n      Error = 8\n      Critical = 9")]
            [NexusObject]
            class Message {
                [NexusKey(0)] public Status Value1;
            }
            [Flags]
            enum Status { Unset = 0, Running = 1 << 0, Stopped = 1 << 2, Error = 1 << 3, Critical = Running | Error }
            """);
    }

    [Test]
    public void CyclicReference_SeenMultipleTimes()
    {
        // Every repeated [NexusObject] is marked [seen], including references back to the root
        Run("""
            using System;
            using NexNet.Serialization;
            [GenerateStructureHash(ExpectedWalk = "Container [NexusObject]\n  Value1: Message [Key:0]\n  Value2: Container [Key:1]\n  Value3: Values [Key:2]\n  Value4: Message [Key:3]\n    Message [NexusObject]\n      Value1: Int32 [Key:0]\n      Value2: Values [Key:1]\n        Values [NexusObject]\n          Value1: Byte[] [Key:0]\n          Value2: Message [Key:1]\n            Byte[] [Array]\n              Byte [SpecialType]\n            Message [seen]\n    Container [seen]\n    Values [seen]\n    Message [seen]")]
            [NexusObject]
            class Container {
                [NexusKey(0)] public Message Value1;
                [NexusKey(1)] public Container Value2;
                [NexusKey(2)] public Values Value3;
                [NexusKey(3)] public Message Value4;
            }
            [NexusObject]
            class Message {
                [NexusKey(0)] public int Value1;
                [NexusKey(1)] public Values Value2;
            }
            [NexusObject]
            class Values {
                [NexusKey(0)] public byte[] Value1;
                [NexusKey(1)] public Message Value2;
            }
            """);
    }

    #endregion

    #region Key Rules

    [Test]
    public void ReorderingDeclarations_WithSameKeys_KeepsHash()
    {
        var a = HashOf("""
            [NexusObject] class Message {
                [NexusKey(0)] public short Value1 { get; set; }
                [NexusKey(1)] public int Value2 { get; set; }
                [NexusKey(2)] public long Value3 { get; set; }
            }
            """);
        var b = HashOf("""
            [NexusObject] class Message {
                [NexusKey(2)] public long Value3 { get; set; }
                [NexusKey(0)] public short Value1 { get; set; }
                [NexusKey(1)] public int Value2 { get; set; }
            }
            """);
        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void FieldsAndProperties_HashTheSame()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1 { get; set; } [NexusKey(1)] public string Value2 { get; set; } }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; [NexusKey(1)] public string Value2; }");
        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void IgnoredAndUnkeyedMembers_DoNotAffectHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; [NexusIgnore] public long Ignored; private int _private; }");
        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void ChangingKey_ChangesHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public short Value1; [NexusKey(1)] public long Value2; }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(1)] public short Value1; [NexusKey(0)] public long Value2; }");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void AddingKey_ChangesHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; [NexusKey(1)] public int Value2; }");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void RemovingKey_ChangesHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; [NexusKey(1)] public int Value2; }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; }");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void KeyGap_IsPartOfHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; [NexusKey(1)] public int Value2; }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; [NexusKey(2)] public int Value2; }");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void ChangingNestedMemberType_ChangesHash()
    {
        var a = HashOf("""
            [NexusObject] class Message { [NexusKey(0)] public Inner Value1; }
            [NexusObject] class Inner { [NexusKey(0)] public int Value1; }
            """);
        var b = HashOf("""
            [NexusObject] class Message { [NexusKey(0)] public Inner Value1; }
            [NexusObject] class Inner { [NexusKey(0)] public long Value1; }
            """);
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void ChangingUnionTag_ChangesHash()
    {
        const string cases = """
            [NexusObject] class A : IMessage { [NexusKey(0)] public int Value; }
            [NexusObject] class B : IMessage { [NexusKey(0)] public string Value; }
            """;
        var a = HashOf("[NexusObject] [NexusUnion<A>(0)] [NexusUnion<B>(1)] interface IMessage { }\n" + cases, "IMessage");
        var b = HashOf("[NexusObject] [NexusUnion<A>(1)] [NexusUnion<B>(0)] interface IMessage { }\n" + cases, "IMessage");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    #endregion

    private static int HashOf(string source, string typeName = "Message")
    {
        var compilation = CSharpGeneratorRunner.CreateCompilation("using NexNet.Serialization;\n" + source);
        return new TypeHasher().GetHash(compilation.GetTypeByMetadataName(typeName)!);
    }

    private void Run(string code)
    {
        var diagnostics = CSharpGeneratorRunner.RunTypeHasherGenerator(
            code + GenerateStructureHashAttribute,
            minDiagnostic: DiagnosticSeverity.Info);

        var failures = diagnostics.Where(d => d.Id.StartsWith("TEST_FAIL")).ToArray();
        Assert.That(failures, Is.Empty, string.Join("\n", failures.Select(d => d.GetMessage())));
    }

    private const string GenerateStructureHashAttribute
        = """
          public class GenerateStructureHashAttribute : Attribute
          {
              public string ExpectedWalk { get; set; }
          }
          """;
}
