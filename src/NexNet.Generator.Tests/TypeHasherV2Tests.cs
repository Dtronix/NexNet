using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

public class TypeHasherV2Tests
{
    [Test]
    public void SimpleType_WithSpecialTypes()
    {
        // Non-MemoryPackable types only show members, SpecialTypes are terminal (not recursively walked)
        Run("""
            using System;
            [GenerateStructureHashV2(ExpectedWalk = "SimpleMessage [NotMemoryPackable]")]
            class SimpleMessage {
                public int Value1;
                public string Value2;
                public bool Value3;
            }
            """);
    }

    [Test]
    public void MemoryPackableType_WithSpecialTypes()
    {
        // SpecialTypes are terminal nodes - they're only shown as member types, not recursively walked
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "SimpleMessage [MemoryPackable]\n  Value1: Int32\n  Value2: String\n  Value3: Boolean")]
            [MemoryPackable]
            partial class SimpleMessage {
                public int Value1;
                public string Value2;
                public bool Value3;
            }
            """);
    }

    [Test]
    public void NullableTypes()
    {
        // Nullable<T> is unwrapped and shown as InnerType?, string? is a SpecialType so not recursively walked
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Value1: Int32?\n  Value2: String?\n    Int32? [Nullable]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public int? Value1;
                public string? Value2;
            }
            """);
    }

    [Test]
    public void ArrayTypes()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Value1: Int32[]\n  Value2: Int32[,]\n  Value3: Int32[,,]\n    Int32[] [Array]\n      Int32 [SpecialType]\n    Int32[,] [Array]\n      Int32 [SpecialType]\n    Int32[,,] [Array]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public int[] Value1;
                public int[,] Value2;
                public int[,,] Value3;
            }
            """);
    }

    [Test]
    public void NullableArrayTypes()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Value1: Int32?[]?\n  Value2: Int32[]?\n    Int32?[]? [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]\n    Int32[]? [Array]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public int?[]? Value1;
                public int[]? Value2;
            }
            """);
    }

    [Test]
    public void GenericTypes_CLR()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Items: List<1>\n    List<1> [CLR]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public List<int> Items;
            }
            """);
    }

    [Test]
    public void GenericTypes_WithUserClass()
    {
        // UserData is walked because it's MemoryPackable, but its Int32 member is terminal
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Items: List<1>\n    List<1> [CLR]\n      UserData [MemoryPackable]\n        Value: Int32")]
            [MemoryPackable]
            partial class Message {
                public List<UserData> Items;
            }
            [MemoryPackable]
            partial class UserData {
                public int Value;
            }
            """);
    }

    [Test]
    public void DictionaryType()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Data: Dictionary<2>\n    Dictionary<2> [CLR]\n      String [SpecialType]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public Dictionary<string, int> Data;
            }
            """);
    }

    [Test]
    public void EnumType()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Status: Status\n    Status [Enum]\n      Pending = 0\n      Running = 1\n      Complete = 2\n      Failed = 3")]
            [MemoryPackable]
            partial class Message {
                public Status Status;
            }
            enum Status { Pending, Running, Complete, Failed }
            """);
    }

    [Test]
    public void EnumType_WithExplicitValues()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Status: Status\n    Status [Enum]\n      None = 0\n      Warning = 10\n      Error = 100\n      Critical = 500")]
            [MemoryPackable]
            partial class Message {
                public Status Status;
            }
            enum Status { None = 0, Warning = 10, Error = 100, Critical = 500 }
            """);
    }

    [Test]
    public void EnumType_WithFlags()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Flags: Permissions\n    Permissions [Enum]\n      None = 0\n      Read = 1\n      Write = 2\n      Execute = 4\n      All = 7")]
            [MemoryPackable]
            partial class Message {
                public Permissions Flags;
            }
            [Flags]
            enum Permissions { None = 0, Read = 1, Write = 2, Execute = 4, All = Read | Write | Execute }
            """);
    }

    [Test]
    public void MemoryPackUnion()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "IMessage [MemoryPackUnion:2]\n  [MPUID:0] MessageA\n  [MPUID:1] MessageB\n    MessageA [MemoryPackable]\n      Value: Int32\n    MessageB [MemoryPackable]\n      Text: String")]
            [MemoryPackable]
            [MemoryPackUnion(0, typeof(MessageA))]
            [MemoryPackUnion(1, typeof(MessageB))]
            partial interface IMessage { }

            [MemoryPackable]
            partial class MessageA : IMessage {
                public int Value;
            }
            [MemoryPackable]
            partial class MessageB : IMessage {
                public string Text;
            }
            """);
    }

    [Test]
    public void MemoryPackUnion_SortsByOrder()
    {
        // Even though attributes are in reverse order, output is sorted by MPUID
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "IMessage [MemoryPackUnion:2]\n  [MPUID:0] MessageA\n  [MPUID:1] MessageB\n    MessageA [MemoryPackable]\n      Value: Int32\n    MessageB [MemoryPackable]\n      Text: String")]
            [MemoryPackable]
            [MemoryPackUnion(1, typeof(MessageB))]
            [MemoryPackUnion(0, typeof(MessageA))]
            partial interface IMessage { }

            [MemoryPackable]
            partial class MessageA : IMessage {
                public int Value;
            }
            [MemoryPackable]
            partial class MessageB : IMessage {
                public string Text;
            }
            """);
    }

    [Test]
    public void MemoryPackOrder_Explicit()
    {
        // Members are sorted by MemoryPackOrder, SpecialTypes are terminal
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  First: Int16 [Order:0]\n  Second: Int32 [Order:1]\n  Third: Int64 [Order:2]")]
            [MemoryPackable]
            partial class Message {
                [MemoryPackOrder(2)] public long Third;
                [MemoryPackOrder(1)] public int Second;
                [MemoryPackOrder(0)] public short First;
            }
            """);
    }

    [Test]
    public void CyclicReference_SelfReferencing()
    {
        // Self-referencing type shows [seen] on second encounter
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Node [MemoryPackable]\n  Value: Int32\n  Next: Node?\n    Node [seen]")]
            [MemoryPackable]
            partial class Node {
                public int Value;
                public Node? Next;
            }
            """);
    }

    [Test]
    public void CyclicReference_MutualReference()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "NodeA [MemoryPackable]\n  Value: Int32\n  Other: NodeB?\n    NodeB? [MemoryPackable]\n      Value: String\n      Other: NodeA?\n        NodeA [seen]")]
            [MemoryPackable]
            partial class NodeA {
                public int Value;
                public NodeB? Other;
            }
            [MemoryPackable]
            partial class NodeB {
                public string Value;
                public NodeA? Other;
            }
            """);
    }

    [Test]
    public void NonMemoryPackable_UserType()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Data: NonSerializable\n    NonSerializable [NotMemoryPackable]")]
            [MemoryPackable]
            partial class Message {
                public NonSerializable Data;
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
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Time: DateTime\n  Id: Guid\n  Span: TimeSpan\n    Guid [CLR]\n    TimeSpan [CLR]")]
            [MemoryPackable]
            partial class Message {
                public DateTime Time;
                public Guid Id;
                public TimeSpan Span;
            }
            """);
    }

    [Test]
    public void NestedTypes()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Outer [MemoryPackable]\n  Inner: InnerType\n    InnerType [MemoryPackable]\n      Value: Int32")]
            [MemoryPackable]
            partial class Outer {
                public InnerType Inner;
            }
            [MemoryPackable]
            partial class InnerType {
                public int Value;
            }
            """);
    }

    [Test]
    public void ComplexNestedGenerics()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Data: Dictionary<2>\n    Dictionary<2> [CLR]\n      String [SpecialType]\n      List<1> [CLR]\n        UserData [MemoryPackable]\n          Id: Int32")]
            [MemoryPackable]
            partial class Message {
                public Dictionary<string, List<UserData>> Data;
            }
            [MemoryPackable]
            partial class UserData {
                public int Id;
            }
            """);
    }

    [Test]
    public void IgnoresPrivateMembers()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  PublicValue: Int32")]
            [MemoryPackable]
            partial class Message {
                public int PublicValue;
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
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  InstanceValue: Int32")]
            [MemoryPackable]
            partial class Message {
                public int InstanceValue;
                public static int StaticValue;
            }
            """);
    }

    [Test]
    public void StructType()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  X: Int32\n  Y: Int32")]
            [MemoryPackable]
            partial struct Message {
                public int X;
                public int Y;
            }
            """);
    }

    #region Deep Nested and Complex Type Tests

    [Test]
    public void DeepNested_ThreeLevels()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Level1 [MemoryPackable]\n  Value: Int32\n  Child: Level2\n    Level2 [MemoryPackable]\n      Value: String\n      Child: Level3\n        Level3 [MemoryPackable]\n          Value: Boolean")]
            [MemoryPackable]
            partial class Level1 {
                public int Value;
                public Level2 Child;
            }
            [MemoryPackable]
            partial class Level2 {
                public string Value;
                public Level3 Child;
            }
            [MemoryPackable]
            partial class Level3 {
                public bool Value;
            }
            """);
    }

    [Test]
    public void DeepNested_FiveLevels_WithNullables()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Root [MemoryPackable]\n  Id: Int32\n  A: LevelA?\n    LevelA? [MemoryPackable]\n      Name: String\n      B: LevelB?\n        LevelB? [MemoryPackable]\n          Count: Int64\n          C: LevelC?\n            LevelC? [MemoryPackable]\n              Flag: Boolean\n              D: LevelD?\n                LevelD? [MemoryPackable]\n                  Data: Double")]
            [MemoryPackable]
            partial class Root {
                public int Id;
                public LevelA? A;
            }
            [MemoryPackable]
            partial class LevelA {
                public string Name;
                public LevelB? B;
            }
            [MemoryPackable]
            partial class LevelB {
                public long Count;
                public LevelC? C;
            }
            [MemoryPackable]
            partial class LevelC {
                public bool Flag;
                public LevelD? D;
            }
            [MemoryPackable]
            partial class LevelD {
                public double Data;
            }
            """);
    }

    [Test]
    public void SelfReferencing_LinkedList()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "LinkedNode [MemoryPackable]\n  Value: Int32\n  Next: LinkedNode?\n  Prev: LinkedNode?\n    LinkedNode [seen]\n    LinkedNode [seen]")]
            [MemoryPackable]
            partial class LinkedNode {
                public int Value;
                public LinkedNode? Next;
                public LinkedNode? Prev;
            }
            """);
    }

    [Test]
    public void SelfReferencing_TreeStructure()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "TreeNode [MemoryPackable]\n  Id: Int32\n  Name: String\n  Parent: TreeNode?\n  Children: List<1>?\n    TreeNode [seen]\n    List<1>? [CLR]\n      TreeNode [seen]")]
            [MemoryPackable]
            partial class TreeNode {
                public int Id;
                public string Name;
                public TreeNode? Parent;
                public List<TreeNode>? Children;
            }
            """);
    }

    [Test]
    public void SelfReferencing_DeepChain_WithArrays()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "ChainNode [MemoryPackable]\n  Data: Int32\n  Next: ChainNode?\n  Siblings: ChainNode[]?\n    ChainNode [seen]\n    ChainNode[]? [Array]\n      ChainNode [seen]")]
            [MemoryPackable]
            partial class ChainNode {
                public int Data;
                public ChainNode? Next;
                public ChainNode[]? Siblings;
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
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Items: List<1>\n    List<1> [CLR]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public List<int> Items;
            }
            """);
    }

    [Test]
    public void Arity_DoubleGeneric()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Map: Dictionary<2>\n    Dictionary<2> [CLR]\n      String [SpecialType]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public Dictionary<string, int> Map;
            }
            """);
    }

    [Test]
    public void Arity_TripleGeneric()
    {
        // Generic types always walk their type arguments, even if the generic itself is not MemoryPackable
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Data: MyTriple<3>\n    MyTriple<3>\n      Int32 [SpecialType]\n      String [SpecialType]\n      Boolean [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public MyTriple<int, string, bool> Data;
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
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "MessageWithList [MemoryPackable]\n  Data: List<1>\n    List<1> [CLR]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class MessageWithList {
                public List<int> Data;
            }
            """);

        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "MessageWithDict [MemoryPackable]\n  Data: Dictionary<2>\n    Dictionary<2> [CLR]\n      Int32 [SpecialType]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class MessageWithDict {
                public Dictionary<int, int> Data;
            }
            """);
    }

    [Test]
    public void Arity_NestedGenerics_DifferentArities()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Level1: List<1>\n  Level2: Dictionary<2>\n    List<1> [CLR]\n      Dictionary<2> [CLR]\n        String [SpecialType]\n        Int32 [SpecialType]\n    Dictionary<2> [CLR]\n      String [SpecialType]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public List<Dictionary<string, int>> Level1;
                public Dictionary<string, int> Level2;
            }
            """);
    }

    [Test]
    public void Arity_GenericWithMemoryPackable()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Items: List<1>\n  Map: Dictionary<2>\n    List<1> [CLR]\n      Inner [MemoryPackable]\n        Value: Int32\n    Dictionary<2> [CLR]\n      String [SpecialType]\n      Inner [seen]")]
            [MemoryPackable]
            partial class Message {
                public List<Inner> Items;
                public Dictionary<string, Inner> Map;
            }
            [MemoryPackable]
            partial class Inner {
                public int Value;
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
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Data: Int32[]\n    Int32[] [Array]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public int[] Data;
            }
            """);
    }

    [Test]
    public void Array_MultiDimension_AllRanks()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Rank1: Int32[]\n  Rank2: Int32[,]\n  Rank3: Int32[,,]\n  Rank4: Int32[,,,]\n    Int32[] [Array]\n      Int32 [SpecialType]\n    Int32[,] [Array]\n      Int32 [SpecialType]\n    Int32[,,] [Array]\n      Int32 [SpecialType]\n    Int32[,,,] [Array]\n      Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public int[] Rank1;
                public int[,] Rank2;
                public int[,,] Rank3;
                public int[,,,] Rank4;
            }
            """);
    }

    [Test]
    public void Array_OfMemoryPackable()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Items: Item[]\n    Item[] [Array]\n      Item [MemoryPackable]\n        Id: Int32\n        Name: String")]
            [MemoryPackable]
            partial class Message {
                public Item[] Items;
            }
            [MemoryPackable]
            partial class Item {
                public int Id;
                public string Name;
            }
            """);
    }

    [Test]
    public void Array_OfArrays_JaggedArrays()
    {
        // Jagged arrays: the outer array type displays without full element type in the walk string
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Jagged: []\n    [] [Array]\n      Int32[] [Array]\n        Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public int[][] Jagged;
            }
            """);
    }

    [Test]
    public void Array_OfNullableElements()
    {
        // string? in an array shows the nullability annotation
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  NullableInts: Int32?[]\n  NullableStrings: String?[]\n    Int32?[] [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]\n    String?[] [Array]\n      String? [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public int?[] NullableInts;
                public string?[] NullableStrings;
            }
            """);
    }

    [Test]
    public void Array_NullableArray_OfNullableElements()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Data: Int32?[]?\n    Int32?[]? [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public int?[]? Data;
            }
            """);
    }

    [Test]
    public void Array_MultiDim_Nullable()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Matrix: Int32?[,]?\n    Int32?[,]? [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public int?[,]? Matrix;
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
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  NInt: Int32?\n  NLong: Int64?\n  NBool: Boolean?\n  NDouble: Double?\n  NDecimal: Decimal?\n    Int32? [Nullable]\n      Int32 [SpecialType]\n    Int64? [Nullable]\n      Int64 [SpecialType]\n    Boolean? [Nullable]\n      Boolean [SpecialType]\n    Double? [Nullable]\n      Double [SpecialType]\n    Decimal? [Nullable]\n      Decimal [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public int? NInt;
                public long? NLong;
                public bool? NBool;
                public double? NDouble;
                public decimal? NDecimal;
            }
            """);
    }

    [Test]
    public void Nullable_ReferenceTypes()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  NullableString: String?\n  NullableObject: Object?")]
            [MemoryPackable]
            partial class Message {
                public string? NullableString;
                public object? NullableObject;
            }
            """);
    }

    [Test]
    public void Nullable_MemoryPackableTypes()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Item: InnerItem?\n    InnerItem? [MemoryPackable]\n      Value: Int32")]
            [MemoryPackable]
            partial class Message {
                public InnerItem? Item;
            }
            [MemoryPackable]
            partial class InnerItem {
                public int Value;
            }
            """);
    }

    [Test]
    public void Nullable_InGenerics()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  NullableList: List<1>?\n  ListOfNullable: List<1>\n    List<1>? [CLR]\n      Int32 [SpecialType]\n    List<1> [CLR]\n      Int32? [Nullable]\n        Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public List<int>? NullableList;
                public List<int?> ListOfNullable;
            }
            """);
    }

    [Test]
    public void Nullable_DictionaryVariations()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Message [MemoryPackable]\n  Dict1: Dictionary<2>?\n  Dict2: Dictionary<2>\n    Dictionary<2>? [CLR]\n      String [SpecialType]\n      Int32 [SpecialType]\n    Dictionary<2> [CLR]\n      String? [SpecialType]\n      Int32? [Nullable]\n        Int32 [SpecialType]")]
            [MemoryPackable]
            partial class Message {
                public Dictionary<string, int>? Dict1;
                public Dictionary<string?, int?> Dict2;
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
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Organization [MemoryPackable]\n  Id: Int32\n  Name: String\n  Root: Department?\n    Department? [MemoryPackable]\n      Id: Int32\n      Name: String\n      Parent: Department?\n      SubDepartments: List<1>?\n      Employees: Employee[]?\n        Department [seen]\n        List<1>? [CLR]\n          Department [seen]\n        Employee[]? [Array]\n          Employee [MemoryPackable]\n            Id: Int32\n            Name: String\n            Manager: Employee?\n              Employee [seen]")]
            [MemoryPackable]
            partial class Organization {
                public int Id;
                public string Name;
                public Department? Root;
            }
            [MemoryPackable]
            partial class Department {
                public int Id;
                public string Name;
                public Department? Parent;
                public List<Department>? SubDepartments;
                public Employee[]? Employees;
            }
            [MemoryPackable]
            partial class Employee {
                public int Id;
                public string Name;
                public Employee? Manager;
            }
            """);
    }

    [Test]
    public void ComplexDeepWalk_GraphWithAllTypes()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Graph [MemoryPackable]\n  Id: Guid\n  Name: String?\n  Metadata: Dictionary<2>?\n  Nodes: GraphNode[]?\n    Guid [CLR]\n    Dictionary<2>? [CLR]\n      String [SpecialType]\n      Object [SpecialType]\n    GraphNode[]? [Array]\n      GraphNode [MemoryPackable]\n        Id: Int32\n        Label: String?\n        Weight: Double?\n        Tags: String[]?\n        Edges: List<1>?\n        Data: NodeData?\n          Double? [Nullable]\n            Double [SpecialType]\n          String[]? [Array]\n            String [SpecialType]\n          List<1>? [CLR]\n            Edge [MemoryPackable]\n              Source: GraphNode?\n              Target: GraphNode?\n              Weight: Decimal?\n                GraphNode [seen]\n                GraphNode [seen]\n                Decimal? [Nullable]\n                  Decimal [SpecialType]\n          NodeData? [MemoryPackable]\n            Values: Int32[]?\n            Matrix: Double[,]?\n            Flags: NodeFlags?\n              Int32[]? [Array]\n                Int32 [SpecialType]\n              Double[,]? [Array]\n                Double [SpecialType]\n              NodeFlags? [Nullable]\n                NodeFlags [Enum]\n                  None = 0\n                  Active = 1\n                  Visited = 2\n                  Processed = 4")]
            [MemoryPackable]
            partial class Graph {
                public Guid Id;
                public string? Name;
                public Dictionary<string, object>? Metadata;
                public GraphNode[]? Nodes;
            }
            [MemoryPackable]
            partial class GraphNode {
                public int Id;
                public string? Label;
                public double? Weight;
                public string[]? Tags;
                public List<Edge>? Edges;
                public NodeData? Data;
            }
            [MemoryPackable]
            partial class Edge {
                public GraphNode? Source;
                public GraphNode? Target;
                public decimal? Weight;
            }
            [MemoryPackable]
            partial class NodeData {
                public int[]? Values;
                public double[,]? Matrix;
                public NodeFlags? Flags;
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
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "IEvent [MemoryPackUnion:3]\n  [MPUID:0] UserEvent\n  [MPUID:1] SystemEvent\n  [MPUID:2] DataEvent\n    UserEvent [MemoryPackable]\n      UserId: Int32\n      Action: String\n      Timestamp: DateTime\n      Metadata: Dictionary<2>?\n        Dictionary<2>? [CLR]\n          String [SpecialType]\n          String [SpecialType]\n    SystemEvent [MemoryPackable]\n      Level: LogLevel\n      Message: String\n      Source: String?\n      StackTrace: String[]?\n        LogLevel [Enum]\n          Debug = 0\n          Info = 1\n          Warning = 2\n          Error = 3\n        String[]? [Array]\n          String [SpecialType]\n    DataEvent [MemoryPackable]\n      EntityId: Guid\n      Changes: FieldChange[]?\n        Guid [CLR]\n        FieldChange[]? [Array]\n          FieldChange [MemoryPackable]\n            FieldName: String\n            OldValue: Object?\n            NewValue: Object?")]
            [MemoryPackable]
            [MemoryPackUnion(0, typeof(UserEvent))]
            [MemoryPackUnion(1, typeof(SystemEvent))]
            [MemoryPackUnion(2, typeof(DataEvent))]
            partial interface IEvent { }

            [MemoryPackable]
            partial class UserEvent : IEvent {
                public int UserId;
                public string Action;
                public DateTime Timestamp;
                public Dictionary<string, string>? Metadata;
            }
            [MemoryPackable]
            partial class SystemEvent : IEvent {
                public LogLevel Level;
                public string Message;
                public string? Source;
                public string[]? StackTrace;
            }
            enum LogLevel { Debug, Info, Warning, Error }
            [MemoryPackable]
            partial class DataEvent : IEvent {
                public Guid EntityId;
                public FieldChange[]? Changes;
            }
            [MemoryPackable]
            partial class FieldChange {
                public string FieldName;
                public object? OldValue;
                public object? NewValue;
            }
            """);
    }

    [Test]
    public void ComplexDeepWalk_AllNullableVariants()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "NullableShowcase [MemoryPackable]\n  NullableInt: Int32?\n  NullableGuid: Guid?\n  NullableEnum: Status?\n  NullableArray: Int32[]?\n  NullableArrayOfNullable: Int32?[]?\n  NullableList: List<1>?\n  NullableDict: Dictionary<2>?\n  NullableNested: NestedNullable?\n    Int32? [Nullable]\n      Int32 [SpecialType]\n    Guid? [Nullable]\n      Guid [CLR]\n    Status? [Nullable]\n      Status [Enum]\n        Pending = 0\n        Active = 1\n        Complete = 2\n    Int32[]? [Array]\n      Int32 [SpecialType]\n    Int32?[]? [Array]\n      Int32? [Nullable]\n        Int32 [SpecialType]\n    List<1>? [CLR]\n      String? [SpecialType]\n    Dictionary<2>? [CLR]\n      String [SpecialType]\n      Inner? [MemoryPackable]\n        Value: Int32?\n          Int32? [Nullable]\n            Int32 [SpecialType]\n    NestedNullable? [MemoryPackable]\n      Deep: DeepNullable?\n        DeepNullable? [MemoryPackable]\n          Values: Int32?[]?\n            Int32?[]? [Array]\n              Int32? [Nullable]\n                Int32 [SpecialType]")]
            [MemoryPackable]
            partial class NullableShowcase {
                public int? NullableInt;
                public Guid? NullableGuid;
                public Status? NullableEnum;
                public int[]? NullableArray;
                public int?[]? NullableArrayOfNullable;
                public List<string?>? NullableList;
                public Dictionary<string, Inner?>? NullableDict;
                public NestedNullable? NullableNested;
            }
            enum Status { Pending, Active, Complete }
            [MemoryPackable]
            partial class Inner {
                public int? Value;
            }
            [MemoryPackable]
            partial class NestedNullable {
                public DeepNullable? Deep;
            }
            [MemoryPackable]
            partial class DeepNullable {
                public int?[]? Values;
            }
            """);
    }

    [Test]
    public void ComplexDeepWalk_SelfReferencingTree_WithAllFeatures()
    {
        Run("""
            using System;
            using System.Collections.Generic;
            using MemoryPack;
            [GenerateStructureHashV2(ExpectedWalk = "Document [MemoryPackable]\n  Id: Guid\n  Title: String\n  Root: Section?\n    Guid [CLR]\n    Section? [MemoryPackable]\n      Id: Int32\n      Title: String\n      Content: String?\n      Parent: Section?\n      Children: Section[]?\n      Annotations: Dictionary<2>?\n      Tags: List<1>?\n      Metadata: SectionMeta?\n        Section [seen]\n        Section[]? [Array]\n          Section [seen]\n        Dictionary<2>? [CLR]\n          String [SpecialType]\n          Annotation [MemoryPackable]\n            Type: AnnotationType\n            Text: String\n            Range: TextRange?\n              AnnotationType [Enum]\n                Comment = 0\n                Highlight = 1\n                Bookmark = 2\n              TextRange? [MemoryPackable]\n                Start: Int32\n                End: Int32?\n                  Int32? [Nullable]\n                    Int32 [SpecialType]\n        List<1>? [CLR]\n          String [SpecialType]\n        SectionMeta? [MemoryPackable]\n          CreatedAt: DateTime\n          ModifiedAt: DateTime?\n          Author: String?\n          Flags: SectionFlags?\n            DateTime? [Nullable]\n              DateTime [SpecialType]\n            SectionFlags? [Nullable]\n              SectionFlags [Enum]\n                None = 0\n                Draft = 1\n                Published = 2\n                Archived = 4")]
            [MemoryPackable]
            partial class Document {
                public Guid Id;
                public string Title;
                public Section? Root;
            }
            [MemoryPackable]
            partial class Section {
                public int Id;
                public string Title;
                public string? Content;
                public Section? Parent;
                public Section[]? Children;
                public Dictionary<string, Annotation>? Annotations;
                public List<string>? Tags;
                public SectionMeta? Metadata;
            }
            [MemoryPackable]
            partial class Annotation {
                public AnnotationType Type;
                public string Text;
                public TextRange? Range;
            }
            enum AnnotationType { Comment, Highlight, Bookmark }
            [MemoryPackable]
            partial class TextRange {
                public int Start;
                public int? End;
            }
            [MemoryPackable]
            partial class SectionMeta {
                public DateTime CreatedAt;
                public DateTime? ModifiedAt;
                public string? Author;
                public SectionFlags? Flags;
            }
            [Flags]
            enum SectionFlags { None = 0, Draft = 1, Published = 2, Archived = 4 }
            """);
    }

    #endregion

    private void Run(string code)
    {
        var diagnostics = CSharpGeneratorRunner.RunTypeHasherV2Generator(
            code + GenerateStructureHashV2Attribute,
            minDiagnostic: DiagnosticSeverity.Info);

        var failures = diagnostics.Where(d => d.Id.StartsWith("TESTV2_FAIL")).ToArray();
        Assert.That(failures, Is.Empty, string.Join('\n', failures.Select(d => d.GetMessage())));
    }

    private const string GenerateStructureHashV2Attribute
        = """
          public class GenerateStructureHashV2Attribute : Attribute
          {
              public string ExpectedWalk { get; set; }
          }
          """;
}
