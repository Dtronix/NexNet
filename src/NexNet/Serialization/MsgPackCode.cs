namespace NexNet.Serialization;

/// <summary>
/// MessagePack type codes (format bytes).
/// </summary>
#pragma warning disable CS1591 // Type codes are named after the MessagePack spec.
public static class MsgPackCode
{
    public const byte MinFixPositive = 0x00;
    public const byte MaxFixPositive = 0x7f;
    public const byte MinFixMap = 0x80;
    public const byte MaxFixMap = 0x8f;
    public const byte MinFixArray = 0x90;
    public const byte MaxFixArray = 0x9f;
    public const byte MinFixStr = 0xa0;
    public const byte MaxFixStr = 0xbf;
    public const byte Nil = 0xc0;
    public const byte NeverUsed = 0xc1;
    public const byte False = 0xc2;
    public const byte True = 0xc3;
    public const byte Bin8 = 0xc4;
    public const byte Bin16 = 0xc5;
    public const byte Bin32 = 0xc6;
    public const byte Ext8 = 0xc7;
    public const byte Ext16 = 0xc8;
    public const byte Ext32 = 0xc9;
    public const byte Float32 = 0xca;
    public const byte Float64 = 0xcb;
    public const byte UInt8 = 0xcc;
    public const byte UInt16 = 0xcd;
    public const byte UInt32 = 0xce;
    public const byte UInt64 = 0xcf;
    public const byte Int8 = 0xd0;
    public const byte Int16 = 0xd1;
    public const byte Int32 = 0xd2;
    public const byte Int64 = 0xd3;
    public const byte FixExt1 = 0xd4;
    public const byte FixExt2 = 0xd5;
    public const byte FixExt4 = 0xd6;
    public const byte FixExt8 = 0xd7;
    public const byte FixExt16 = 0xd8;
    public const byte Str8 = 0xd9;
    public const byte Str16 = 0xda;
    public const byte Str32 = 0xdb;
    public const byte Array16 = 0xdc;
    public const byte Array32 = 0xdd;
    public const byte Map16 = 0xde;
    public const byte Map32 = 0xdf;
    public const byte MinFixNegative = 0xe0;
    public const byte MaxFixNegative = 0xff;

    /// <summary>
    /// Maximum element count in a fixarray / fixmap header.
    /// </summary>
    public const int MaxFixCollectionCount = 15;

    /// <summary>
    /// Maximum byte length of a fixstr.
    /// </summary>
    public const int MaxFixStrLength = 31;
}
