using NexNet.Generator.Serialization;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

public class TypeHasherTests
{
    [Test]
    public void SimpleType_WithSpecialTypes()
    {
        // Types without [NexusObject] are hashed by name only; their members are not walked
        AssertWalk("""
            using System;
            class SimpleMessage {
                public int Value1;
                public string Value2;
                public bool Value3;
            }
            """, "SimpleMessage", "root: SimpleMessage");
    }

    [Test]
    public void NexusObjectType_WithSpecialTypes()
    {
        // SpecialTypes are terminal nodes - they're only shown as member types, not recursively walked
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class SimpleMessage {
                [NexusKey(0)] public int Value1;
                [NexusKey(1)] public string Value2;
                [NexusKey(2)] public bool Value3;
            }
            """, "SimpleMessage", "root: #0\n#0 object SimpleMessage\n  0: Int32\n  1: String\n  2: Boolean");
    }

    [Test]
    public void NullableTypes()
    {
        // Nullable<T> is unwrapped and shown as InnerType?, string? is a SpecialType so not recursively walked
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int? Value1;
                [NexusKey(1)] public string? Value2;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32?\n  1: String");
    }

    [Test]
    public void ArrayTypes()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int[] Value1;
                [NexusKey(1)] public int[,] Value2;
                [NexusKey(2)] public int[,,] Value3;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32[]\n  1: Int32[,]\n  2: Int32[,,]");
    }

    [Test]
    public void NullableArrayTypes()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int?[]? Value1;
                [NexusKey(1)] public int[]? Value2;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32?[]\n  1: Int32[]");
    }

    [Test]
    public void GenericTypes_CLR()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<int> Items;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: List<Int32>");
    }

    [Test]
    public void GenericTypes_WithUserClass()
    {
        // UserData is walked because it's a [NexusObject], but its Int32 member is terminal
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<UserData> Items;
            }
            [NexusObject]
            partial class UserData {
                [NexusKey(0)] public int Value;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: List<#1>\n#1 object UserData\n  0: Int32");
    }

    [Test]
    public void DictionaryType()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Dictionary<string, int> Data;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Dictionary<String, Int32>");
    }

    [Test]
    public void EnumType()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Status Status;
            }
            enum Status { Pending, Running, Complete, Failed }
            """, "Message", "root: #0\n#0 object Message\n  0: enum Int32 {0, 1, 2, 3}");
    }

    [Test]
    public void EnumType_WithExplicitValues()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Status Status;
            }
            enum Status { None = 0, Warning = 10, Error = 100, Critical = 500 }
            """, "Message", "root: #0\n#0 object Message\n  0: enum Int32 {0, 10, 100, 500}");
    }

    [Test]
    public void EnumType_WithFlags()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Permissions Flags;
            }
            [Flags]
            enum Permissions { None = 0, Read = 1, Write = 2, Execute = 4, All = Read | Write | Execute }
            """, "Message", "root: #0\n#0 object Message\n  0: enum Int32 {0, 1, 2, 4, 7}");
    }

    [Test]
    public void NexusUnion()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
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
            """, "IMessage", "root: #0\n#0 union IMessage\n  tag 0: #1\n  tag 1: #2\n#1 object MessageA\n  0: Int32\n#2 object MessageB\n  0: String");
    }

    [Test]
    public void NexusUnion_SortsByTag()
    {
        // Even though attributes are in reverse order, output is sorted by union tag
        AssertWalk("""
            using System;
            using NexNet.Serialization;
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
            """, "IMessage", "root: #0\n#0 union IMessage\n  tag 0: #1\n  tag 1: #2\n#1 object MessageA\n  0: Int32\n#2 object MessageB\n  0: String");
    }

    [Test]
    public void NexusKey_MembersSortedByKey()
    {
        // Members are sorted by NexusKey, not declaration order; SpecialTypes are terminal
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(2)] public long Third;
                [NexusKey(1)] public int Second;
                [NexusKey(0)] public short First;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int16\n  1: Int32\n  2: Int64");
    }

    [Test]
    public void CyclicReference_SelfReferencing()
    {
        // Self-referencing type shows [seen] on second encounter
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Node {
                [NexusKey(0)] public int Value;
                [NexusKey(1)] public Node? Next;
            }
            """, "Node", "root: #0\n#0 object Node\n  0: Int32\n  1: #0");
    }

    [Test]
    public void CyclicReference_MutualReference()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
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
            """, "NodeA", "root: #0\n#0 object NodeA\n  0: Int32\n  1: #1\n#1 object NodeB\n  0: String\n  1: #0");
    }

    [Test]
    public void NonNexusObject_UserType()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public NonSerializable Data;
            }
            class NonSerializable {
                public int Value1;
                public string Value2;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: NonSerializable");
    }

    [Test]
    public void CLRType_SystemNamespace()
    {
        // DateTime is a SpecialType in Roslyn (terminal, not walked)
        // Guid and TimeSpan are CLR types (walked, shown as [CLR])
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public DateTime Time;
                [NexusKey(1)] public Guid Id;
                [NexusKey(2)] public TimeSpan Span;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: DateTime\n  1: Guid\n  2: TimeSpan");
    }

    [Test]
    public void NestedTypes()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Outer {
                [NexusKey(0)] public InnerType Inner;
            }
            [NexusObject]
            partial class InnerType {
                [NexusKey(0)] public int Value;
            }
            """, "Outer", "root: #0\n#0 object Outer\n  0: #1\n#1 object InnerType\n  0: Int32");
    }

    [Test]
    public void ComplexNestedGenerics()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Dictionary<string, List<UserData>> Data;
            }
            [NexusObject]
            partial class UserData {
                [NexusKey(0)] public int Id;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Dictionary<String, List<#1>>\n#1 object UserData\n  0: Int32");
    }

    [Test]
    public void IgnoresPrivateMembers()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int PublicValue;
                private int PrivateValue;
                protected int ProtectedValue;
                internal int InternalValue;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32");
    }

    [Test]
    public void IgnoresStaticMembers()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int InstanceValue;
                public static int StaticValue;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32");
    }

    [Test]
    public void StructType()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial struct Message {
                [NexusKey(0)] public int X;
                [NexusKey(1)] public int Y;
            }
            """, "Message", "root: #0\n#0 struct Message\n  0: Int32\n  1: Int32");
    }

    #region Deep Nested and Complex Type Tests

    [Test]
    public void DeepNested_ThreeLevels()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
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
            """, "Level1", "root: #0\n#0 object Level1\n  0: Int32\n  1: #1\n#1 object Level2\n  0: String\n  1: #2\n#2 object Level3\n  0: Boolean");
    }

    [Test]
    public void DeepNested_FiveLevels_WithNullables()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
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
            """, "Root", "root: #0\n#0 object Root\n  0: Int32\n  1: #1\n#1 object LevelA\n  0: String\n  1: #2\n#2 object LevelB\n  0: Int64\n  1: #3\n#3 object LevelC\n  0: Boolean\n  1: #4\n#4 object LevelD\n  0: Double");
    }

    [Test]
    public void SelfReferencing_LinkedList()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class LinkedNode {
                [NexusKey(0)] public int Value;
                [NexusKey(1)] public LinkedNode? Next;
                [NexusKey(2)] public LinkedNode? Prev;
            }
            """, "LinkedNode", "root: #0\n#0 object LinkedNode\n  0: Int32\n  1: #0\n  2: #0");
    }

    [Test]
    public void SelfReferencing_TreeStructure()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class TreeNode {
                [NexusKey(0)] public int Id;
                [NexusKey(1)] public string Name;
                [NexusKey(2)] public TreeNode? Parent;
                [NexusKey(3)] public List<TreeNode>? Children;
            }
            """, "TreeNode", "root: #0\n#0 object TreeNode\n  0: Int32\n  1: String\n  2: #0\n  3: List<#0>");
    }

    [Test]
    public void SelfReferencing_DeepChain_WithArrays()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class ChainNode {
                [NexusKey(0)] public int Data;
                [NexusKey(1)] public ChainNode? Next;
                [NexusKey(2)] public ChainNode[]? Siblings;
            }
            """, "ChainNode", "root: #0\n#0 object ChainNode\n  0: Int32\n  1: #0\n  2: #0[]");
    }

    #endregion

    #region Arity Tests

    [Test]
    public void Arity_SingleGeneric()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<int> Items;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: List<Int32>");
    }

    [Test]
    public void Arity_DoubleGeneric()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Dictionary<string, int> Map;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Dictionary<String, Int32>");
    }

    [Test]
    public void Arity_TripleGeneric()
    {
        // Generic types always walk their type arguments, even if the generic itself is not a [NexusObject]
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public MyTriple<int, string, bool> Data;
            }
            class MyTriple<T1, T2, T3> {
                public T1 First;
                public T2 Second;
                public T3 Third;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: MyTriple<Int32, String, Boolean>");
    }

    [Test]
    public void Arity_DifferentArities_DifferentHashes()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class MessageWithList {
                [NexusKey(0)] public List<int> Data;
            }
            """, "MessageWithList", "root: #0\n#0 object MessageWithList\n  0: List<Int32>");

        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class MessageWithDict {
                [NexusKey(0)] public Dictionary<int, int> Data;
            }
            """, "MessageWithDict", "root: #0\n#0 object MessageWithDict\n  0: Dictionary<Int32, Int32>");
    }

    [Test]
    public void Arity_NestedGenerics_DifferentArities()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<Dictionary<string, int>> Level1;
                [NexusKey(1)] public Dictionary<string, int> Level2;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: List<Dictionary<String, Int32>>\n  1: Dictionary<String, Int32>");
    }

    [Test]
    public void Arity_GenericWithNexusObject()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<Inner> Items;
                [NexusKey(1)] public Dictionary<string, Inner> Map;
            }
            [NexusObject]
            partial class Inner {
                [NexusKey(0)] public int Value;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: List<#1>\n  1: Dictionary<String, #1>\n#1 object Inner\n  0: Int32");
    }

    #endregion

    #region Array Variations

    [Test]
    public void Array_SingleDimension()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int[] Data;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32[]");
    }

    [Test]
    public void Array_MultiDimension_AllRanks()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int[] Rank1;
                [NexusKey(1)] public int[,] Rank2;
                [NexusKey(2)] public int[,,] Rank3;
                [NexusKey(3)] public int[,,,] Rank4;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32[]\n  1: Int32[,]\n  2: Int32[,,]\n  3: Int32[,,,]");
    }

    [Test]
    public void Array_OfNexusObject()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Item[] Items;
            }
            [NexusObject]
            partial class Item {
                [NexusKey(0)] public int Id;
                [NexusKey(1)] public string Name;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: #1[]\n#1 object Item\n  0: Int32\n  1: String");
    }

    [Test]
    public void Array_OfArrays_JaggedArrays()
    {
        // Jagged arrays: the outer array type displays without full element type in the walk string
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int[][] Jagged;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32[][]");
    }

    [Test]
    public void Array_OfNullableElements()
    {
        // string? in an array shows the nullability annotation
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int?[] NullableInts;
                [NexusKey(1)] public string?[] NullableStrings;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32?[]\n  1: String[]");
    }

    [Test]
    public void Array_NullableArray_OfNullableElements()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int?[]? Data;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32?[]");
    }

    [Test]
    public void Array_MultiDim_Nullable()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int?[,]? Matrix;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32?[,]");
    }

    #endregion

    #region Nullable Variations

    [Test]
    public void Nullable_PrimitiveTypes()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public int? NInt;
                [NexusKey(1)] public long? NLong;
                [NexusKey(2)] public bool? NBool;
                [NexusKey(3)] public double? NDouble;
                [NexusKey(4)] public decimal? NDecimal;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32?\n  1: Int64?\n  2: Boolean?\n  3: Double?\n  4: Decimal?");
    }

    [Test]
    public void Nullable_ReferenceTypes()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public string? NullableString;
                [NexusKey(1)] public object? NullableObject;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: String\n  1: Object");
    }

    [Test]
    public void Nullable_NexusObjectTypes()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public InnerItem? Item;
            }
            [NexusObject]
            partial class InnerItem {
                [NexusKey(0)] public int Value;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: #1\n#1 object InnerItem\n  0: Int32");
    }

    [Test]
    public void Nullable_InGenerics()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public List<int>? NullableList;
                [NexusKey(1)] public List<int?> ListOfNullable;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: List<Int32>\n  1: List<Int32?>");
    }

    [Test]
    public void Nullable_DictionaryVariations()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            partial class Message {
                [NexusKey(0)] public Dictionary<string, int>? Dict1;
                [NexusKey(1)] public Dictionary<string?, int?> Dict2;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Dictionary<String, Int32>\n  1: Dictionary<String, Int32?>");
    }

    #endregion

    #region Complex Deep Walk Scenarios

    [Test]
    public void ComplexDeepWalk_TreeWithCollections()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
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
            """, "Organization", "root: #0\n#0 object Organization\n  0: Int32\n  1: String\n  2: #1\n#1 object Department\n  0: Int32\n  1: String\n  2: #1\n  3: List<#1>\n  4: #2[]\n#2 object Employee\n  0: Int32\n  1: String\n  2: #2");
    }

    [Test]
    public void ComplexDeepWalk_GraphWithAllTypes()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
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
            """, "Graph", "root: #0\n#0 object Graph\n  0: Guid\n  1: String\n  2: Dictionary<String, Object>\n  3: #1[]\n#1 object GraphNode\n  0: Int32\n  1: String\n  2: Double?\n  3: String[]\n  4: List<#2>\n  5: #3\n#2 object Edge\n  0: #1\n  1: #1\n  2: Decimal?\n#3 object NodeData\n  0: Int32[]\n  1: Double[,]\n  2: enum Int32 {0, 1, 2, 4}?");
    }

    [Test]
    public void ComplexDeepWalk_UnionWithDeepTypes()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
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
            """, "IEvent", "root: #0\n#0 union IEvent\n  tag 0: #1\n  tag 1: #2\n  tag 2: #3\n#1 object UserEvent\n  0: Int32\n  1: String\n  2: DateTime\n  3: Dictionary<String, String>\n#2 object SystemEvent\n  0: enum Int32 {0, 1, 2, 3}\n  1: String\n  2: String\n  3: String[]\n#3 object DataEvent\n  0: Guid\n  1: #4[]\n#4 object FieldChange\n  0: String\n  1: Object\n  2: Object");
    }

    [Test]
    public void ComplexDeepWalk_AllNullableVariants()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
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
            """, "NullableShowcase", "root: #0\n#0 object NullableShowcase\n  0: Int32?\n  1: Guid?\n  2: enum Int32 {0, 1, 2}?\n  3: Int32[]\n  4: Int32?[]\n  5: List<String>\n  6: Dictionary<String, #1>\n  7: #2\n#1 object Inner\n  0: Int32?\n#2 object NestedNullable\n  0: #3\n#3 object DeepNullable\n  0: Int32?[]");
    }

    [Test]
    public void ComplexDeepWalk_SelfReferencingTree_WithAllFeatures()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
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
            """, "Document", "root: #0\n#0 object Document\n  0: Guid\n  1: String\n  2: #1\n#1 object Section\n  0: Int32\n  1: String\n  2: String\n  3: #1\n  4: #1[]\n  5: Dictionary<String, #2>\n  6: List<String>\n  7: #4\n#2 object Annotation\n  0: enum Int32 {0, 1, 2}\n  1: String\n  2: #3\n#3 object TextRange\n  0: Int32\n  1: Int32?\n#4 object SectionMeta\n  0: DateTime\n  1: DateTime?\n  2: String\n  3: enum Int32 {0, 1, 2, 4}?");
    }

    #endregion

    #region Built-in Types

    [Test]
    public void NullableOfT_AndQuestionMark_WalkTheSame()
    {
        // Nullable<int> and int? are the same type; both spellings produce the same member lines
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            class Message {
                [NexusKey(0)] public Nullable<int>[] Value1;
                [NexusKey(1)] public int?[] Value2;
                [NexusKey(2)] public Nullable<int> Value3;
                [NexusKey(3)] public int? Value4;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: Int32?[]\n  1: Int32?[]\n  2: Int32?\n  3: Int32?");
    }

    [Test]
    public void Tuples_WalkTypeArguments()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            class Message {
                [NexusKey(0)] public ValueTuple<int> Value1;
                [NexusKey(1)] public ValueTuple<Tuple<int>> Value2;
                [NexusKey(2)] public Tuple<int, string> Value3;
                [NexusKey(3)] public Tuple<ValueTuple<int, long>> Value4;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: ValueTuple<Int32>\n  1: ValueTuple<Tuple<Int32>>\n  2: Tuple<Int32, String>\n  3: Tuple<ValueTuple<Int32, Int64>>");
    }

    [Test]
    public void NestedCollectionsAndTuples()
    {
        AssertWalk("""
            using System;
            using System.Collections.Generic;
            using NexNet.Serialization;
            [NexusObject]
            class Message {
                [NexusKey(0)] public List<ValueTuple<List<Dictionary<byte, int>>, string?, int[], int[]?>> Value1;
            }
            """, "Message", "root: #0\n#0 object Message\n  0: List<ValueTuple<List<Dictionary<Byte, Int32>>, String, Int32[], Int32[]>>");
    }

    [Test]
    public void BuiltInLeafTypes()
    {
        // Types with built-in formatters are hashed by name; System types are not walked
        AssertWalk("""
            using System;
            using System.Numerics;
            using NexNet.Serialization;
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
            """, "Message", "root: #0\n#0 object Message\n  0: String\n  1: Decimal\n  2: DateTime\n  3: DateTimeOffset\n  4: TimeSpan\n  5: DateOnly\n  6: TimeOnly\n  7: Guid\n  8: Half\n  9: BigInteger\n  10: Uri\n  11: Version\n  12: Byte[]");
    }

    [Test]
    public void BuiltInGenericTypes()
    {
        AssertWalk("""
            using System;
            using System.Buffers;
            using System.Collections.Generic;
            using NexNet.Serialization;
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
            """, "Message", "root: #0\n#0 object Message\n  0: Memory<Byte>\n  1: ReadOnlyMemory<Int32>\n  2: ArraySegment<Byte>\n  3: ReadOnlySequence<Byte>\n  4: KeyValuePair<Byte, Int32>\n  5: HashSet<Int32>\n  6: Queue<Int32>\n  7: Stack<Int32>\n  8: IList<Int32>\n  9: IReadOnlyList<Int32>\n  10: ICollection<Int32>\n  11: IReadOnlyCollection<Int32>\n  12: IEnumerable<Int32>\n  13: IDictionary<Int32, Int64>\n  14: IReadOnlyDictionary<Int32, Int64>");
    }

    [Test]
    public void Enum_MembersSortedByValue()
    {
        // Enum members are walked in value order, not declaration order
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            class Message {
                [NexusKey(0)] public Status Value1;
            }
            enum Status { Unset = 0, Running = 10, Stopped = 20, Error = 100, EOF = 1 }
            """, "Message", "root: #0\n#0 object Message\n  0: enum Int32 {0, 1, 10, 20, 100}");
    }

    [Test]
    public void Enum_FlagsWithCombinedValues()
    {
        AssertWalk("""
            using System;
            using NexNet.Serialization;
            [NexusObject]
            class Message {
                [NexusKey(0)] public Status Value1;
            }
            [Flags]
            enum Status { Unset = 0, Running = 1 << 0, Stopped = 1 << 2, Error = 1 << 3, Critical = Running | Error }
            """, "Message", "root: #0\n#0 object Message\n  0: enum Int32 {0, 1, 4, 8, 9}");
    }

    [Test]
    public void CyclicReference_SeenMultipleTimes()
    {
        // Every repeated [NexusObject] is marked [seen], including references back to the root
        AssertWalk("""
            using System;
            using NexNet.Serialization;
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
            """, "Container", "root: #0\n#0 object Container\n  0: #1\n  1: #0\n  2: #2\n  3: #1\n#1 object Message\n  0: Int32\n  1: #2\n#2 object Values\n  0: Byte[]\n  1: #1");
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

    #region Hash Rules

    [Test]
    public void RenamingType_KeepsHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; [NexusKey(1)] public string Value2; }");
        var b = HashOf("[NexusObject] class Envelope { [NexusKey(0)] public int Value1; [NexusKey(1)] public string Value2; }", "Envelope");
        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void RenamingMember_KeepsHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; [NexusKey(1)] public string Value2; }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Count; [NexusKey(1)] public string Text; }");
        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void RenamingEnumMember_KeepsHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public Kind Value; } enum Kind { A, B }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public Kind Value; } enum Kind { X, Y }");
        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void ChangingEnumValue_ChangesHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public Kind Value; } enum Kind { A = 0, B = 1 }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public Kind Value; } enum Kind { A = 0, B = 2 }");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void ChangingEnumUnderlyingType_ChangesHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public Kind Value; } enum Kind : int { A, B }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public Kind Value; } enum Kind : long { A, B }");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void ReferenceNullability_KeepsHash()
    {
        var a = HashOf("""
            #nullable enable
            [NexusObject] class Message { [NexusKey(0)] public string Text = ""; [NexusKey(1)] public Inner Value = new(); }
            [NexusObject] class Inner { [NexusKey(0)] public int Value1; }
            """);
        var b = HashOf("""
            #nullable enable
            [NexusObject] class Message { [NexusKey(0)] public string? Text; [NexusKey(1)] public Inner? Value; }
            [NexusObject] class Inner { [NexusKey(0)] public int Value1; }
            """);
        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void ValueNullability_ChangesHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public int? Value1; }");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void ClassToStruct_ChangesHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; }");
        var b = HashOf("[NexusObject] struct Message { [NexusKey(0)] public int Value1; }");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void StructurallyIdenticalTypes_HashEqual()
    {
        const string source = """
            [NexusObject] class Order { [NexusKey(0)] public int Id; [NexusKey(1)] public string Note; }
            [NexusObject] class Invoice { [NexusKey(0)] public int Number; [NexusKey(1)] public string Memo; }
            """;
        Assert.That(HashOf(source, "Invoice"), Is.EqualTo(HashOf(source, "Order")));
    }

    [Test]
    public void GenericNexusObjectMemberChange_ChangesHash()
    {
        var a = HashOf("""
            [NexusObject] class Message { [NexusKey(0)] public Envelope<int> Value; }
            [NexusObject] class Envelope<T> { [NexusKey(0)] public T Value; }
            """);
        var b = HashOf("""
            [NexusObject] class Message { [NexusKey(0)] public Envelope<int> Value; }
            [NexusObject] class Envelope<T> { [NexusKey(0)] public T Value; [NexusKey(1)] public int Extra; }
            """);
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void NexusIgnoredKeyedMember_KeepsHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public int Value1; [NexusKey(1)][NexusIgnore] public int X; }");
        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void CycleShape_ChangesHash()
    {
        var a = HashOf("[NexusObject] class Node { [NexusKey(0)] public int Value; [NexusKey(1)] public Node? Next; }", "Node");
        var b = HashOf("[NexusObject] class Node { [NexusKey(0)] public int Value; [NexusKey(1)] public int Next; }", "Node");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    [Test]
    public void ListToArray_ChangesHash()
    {
        var a = HashOf("[NexusObject] class Message { [NexusKey(0)] public System.Collections.Generic.List<int> Values; }");
        var b = HashOf("[NexusObject] class Message { [NexusKey(0)] public int[] Values; }");
        Assert.That(b, Is.Not.EqualTo(a));
    }

    #endregion

    private static (int Hash, string Listing) Walk(string source, string typeName)
    {
        var compilation = CSharpGeneratorRunner.CreateCompilation(source);
        var type = compilation.GetTypeByMetadataName(typeName)
                   ?? throw new AssertionException($"Type '{typeName}' not found.");
        return ShapeHasher.HashWithListing(new ShapeBuilder(compilation).Get(type));
    }

    private static void AssertWalk(string source, string typeName, string expected)
    {
        var actual = Walk(source, typeName).Listing;
        // On a mismatch, print the full listing as a C# literal (NUnit truncates long strings in its own message).
        Assert.That(actual, Is.EqualTo(expected.ReplaceLineEndings("\n")),
            "Actual listing: \"" + actual.Replace("\n", "\\n") + "\"");
    }

    private static int HashOf(string source, string typeName = "Message")
        => Walk("using NexNet.Serialization;\n" + source, typeName).Hash;
}
