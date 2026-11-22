import { describe, expect, it } from 'vitest';
import {
  DateTimeKind, EMPTY_GUID, MAX_TICKS, NexusDateOnly, NexusDateTime, NexusDateTimeOffset, NexusDecimal, NexusTimeOnly,
  NexusTimeSpan, NexusVersion, guidFromBytes, guidFromDotNetBytes, guidToBytes, guidToDotNetBytes, parseGuid, tuple,
} from '../../src/values/index.js';
import { toHex } from './support/golden.js';

describe('NexusDecimal', () => {
  it('formats as C# decimal.ToString(InvariantCulture)', () => {
    expect(new NexusDecimal(15n, 1, true).toString()).toBe('-1.5');
    expect(new NexusDecimal(150n, 2, false).toString()).toBe('1.50');
    expect(new NexusDecimal(15n, 3, true).toString()).toBe('-0.015');
    expect(new NexusDecimal(0n, 1, true).toString()).toBe('0.0');
    expect(new NexusDecimal(1n, 28, false).toString()).toBe('0.0000000000000000000000000001');
    expect(new NexusDecimal(2n ** 96n - 1n, 0, false).toString()).toBe('79228162514264337593543950335');
  });

  it('parses, keeping the scale and the sign of zero', () => {
    const d = NexusDecimal.parse('1.50');
    expect([d.magnitude, d.scale, d.negative]).toEqual([150n, 2, false]);
    expect(NexusDecimal.parse('-0.0').toBits()).toEqual([0, 0, 0, -2147418112]);
    expect(NexusDecimal.parse('1e3').toBits()).toEqual([1000, 0, 0, 0]);
    expect(NexusDecimal.parse(' +.5 ').toString()).toBe('0.5');
    expect(NexusDecimal.parse('0.00000000000000000000000000015').toString()).toBe('0.0000000000000000000000000002');
    expect(() => NexusDecimal.parse('abc')).toThrow(SyntaxError);
    expect(() => NexusDecimal.parse('.')).toThrow(SyntaxError);
    expect(() => NexusDecimal.parse('79228162514264337593543950336')).toThrow(RangeError);
  });

  it('converts to and from GetBits', () => {
    const max = NexusDecimal.fromBits(-1, -1, -1, 0);
    expect(max.magnitude).toBe(2n ** 96n - 1n);
    expect(max.toBits()).toEqual([-1, -1, -1, 0]);
    expect(NexusDecimal.fromBits(15, 0, 0, -2147418112).toString()).toBe('-1.5');
    expect(() => NexusDecimal.fromBits(1, 0, 0, 29 << 16)).toThrow(RangeError);
    expect(() => NexusDecimal.fromBits(1, 0, 0, 1)).toThrow(RangeError);
    expect(() => new NexusDecimal(2n ** 96n, 0, false)).toThrow(RangeError);
  });

  it('compares numerically and converts to number', () => {
    expect(NexusDecimal.parse('1.0').equals(NexusDecimal.parse('1.00'))).toBe(true);
    expect(NexusDecimal.parse('0').equals(NexusDecimal.parse('-0.0'))).toBe(true);
    expect(NexusDecimal.parse('1').equals(NexusDecimal.parse('-1'))).toBe(false);
    expect(NexusDecimal.parse('123.456').toNumber()).toBe(123.456);
    expect(Object.is(NexusDecimal.parse('-0').toNumber(), -0)).toBe(true);
    expect(NexusDecimal.fromNumber(0.1).toString()).toBe('0.1');
    expect(NexusDecimal.fromNumber(1e21).toString()).toBe('1000000000000000000000');
  });
});

describe('NexusDateTime', () => {
  it('converts to and from ToBinary', () => {
    expect(NexusDateTime.fromBinary(0x48d78e4d8b7c0000n).kind).toBe(DateTimeKind.Utc);
    const local = NexusDateTime.fromBinary(BigInt.asIntN(64, 0x88d78e4d8b7c0000n));
    expect([local.kind, local.ticks]).toEqual([DateTimeKind.Local, 0x08d78e4d8b7c0000n]);
    expect(local.toBinary()).toBe(BigInt.asIntN(64, 0x88d78e4d8b7c0000n));
    // Kind 11 (the .NET ambiguous-DST flag) reads as Local.
    expect(NexusDateTime.fromBinary(BigInt.asIntN(64, 0xc8d78e4d8b7c0000n)).kind).toBe(DateTimeKind.Local);
    // A local time just after 0001-01-01 east of UTC: negative UTC ticks, stored as 2^62 + ticks.
    const early = new NexusDateTime(-36_000_000_000n, DateTimeKind.Local);
    expect(NexusDateTime.fromBinary(early.toBinary()).equals(early)).toBe(true);
    expect(() => NexusDateTime.fromBinary(MAX_TICKS + 1n)).toThrow(RangeError);
    expect(() => new NexusDateTime(-1n)).toThrow(RangeError);
  });

  it('converts to and from Date', () => {
    const date = new Date(Date.UTC(2020, 0, 1));
    const dt = NexusDateTime.fromDate(date);
    expect([dt.ticks, dt.kind]).toEqual([637134336000000000n, DateTimeKind.Utc]);
    expect(dt.toDate().getTime()).toBe(date.getTime());
    expect(new NexusDateTime(0n).toDate().toISOString()).toBe('0001-01-01T00:00:00.000Z');
    expect(new NexusDateTime(1n).toDate().getTime()).toBe(new NexusDateTime(0n).toDate().getTime());
    expect(dt.equals(new NexusDateTime(dt.ticks, DateTimeKind.Unspecified))).toBe(false);
  });
});

describe('NexusDateTimeOffset', () => {
  it('holds clock ticks and sends UTC ticks', () => {
    const v = NexusDateTimeOffset.fromUtcTicks(637134336000000000n, 330);
    expect(v.ticks).toBe(637134336000000000n + 330n * 600_000_000n);
    expect(v.utcTicks).toBe(637134336000000000n);
    expect(v.equals(NexusDateTimeOffset.fromUtcTicks(637134336000000000n, -480))).toBe(true);
    expect(NexusDateTimeOffset.fromDate(new Date(Date.UTC(2020, 0, 1)), 60).toDate().getTime()).toBe(Date.UTC(2020, 0, 1));
    expect(() => NexusDateTimeOffset.fromUtcTicks(0n, 841)).toThrow(RangeError);
    expect(() => NexusDateTimeOffset.fromUtcTicks(0n, -60)).toThrow(RangeError);
  });
});

describe('TimeSpan, DateOnly, TimeOnly', () => {
  it('convert', () => {
    expect(NexusTimeSpan.fromMilliseconds(1.5).ticks).toBe(15_000n);
    expect(new NexusTimeSpan(-15_000n).totalMilliseconds).toBe(-1.5);
    expect(NexusDateOnly.fromParts(1, 1, 1).dayNumber).toBe(0);
    expect(NexusDateOnly.fromParts(2020, 1, 1).dayNumber).toBe(737424);
    expect(NexusDateOnly.fromParts(9999, 12, 31).dayNumber).toBe(3652058);
    const d = new NexusDateOnly(737424);
    expect([d.year, d.month, d.day]).toEqual([2020, 1, 1]);
    expect(() => NexusDateOnly.fromParts(2021, 2, 29)).toThrow(RangeError);
    expect(() => new NexusDateOnly(3652059)).toThrow(RangeError);
    expect(NexusTimeOnly.fromParts(12, 34, 56, 789).ticks).toBe(452967890000n);
    expect(() => new NexusTimeOnly(864_000_000_000n)).toThrow(RangeError);
    expect(new NexusTimeOnly(1n).equals(new NexusTimeOnly(1n))).toBe(true);
  });
});

describe('NexusVersion', () => {
  it('parses 2 to 4 parts and formats as C#', () => {
    expect(NexusVersion.parse('1.2').toString()).toBe('1.2');
    expect(NexusVersion.parse('1.2.3.4').toString()).toBe('1.2.3.4');
    const v = NexusVersion.parse('1.2.3');
    expect([v.major, v.minor, v.build, v.revision]).toEqual([1, 2, 3, -1]);
    expect(v.equals(new NexusVersion(1, 2, 3))).toBe(true);
    for (const bad of ['1', 'x', '1.2.3.4.5', '1.-2', '1.2147483648', '1..2']) expect(() => NexusVersion.parse(bad), bad).toThrow(SyntaxError);
    expect(() => new NexusVersion(1, 2, -1, 4)).toThrow(RangeError);
  });
});

describe('Guid', () => {
  it('converts between text, RFC 4122 bytes and .NET bytes', () => {
    const g = '00112233-4455-6677-8899-aabbccddeeff';
    expect(toHex(guidToBytes(g))).toBe('00112233445566778899aabbccddeeff');
    expect(guidFromBytes(guidToBytes(g))).toBe(g);
    expect(toHex(guidToDotNetBytes(g))).toBe('33221100554477668899aabbccddeeff');
    expect(guidFromDotNetBytes(guidToDotNetBytes(g))).toBe(g);
    expect(parseGuid('00112233445566778899AABBCCDDEEFF')).toBe(g);
    expect(parseGuid(EMPTY_GUID)).toBe(EMPTY_GUID);
    expect(() => parseGuid('0011')).toThrow(SyntaxError);
    expect(() => guidFromBytes(new Uint8Array(15))).toThrow(RangeError);
  });
});

it('tuple returns its items', () => {
  expect(tuple(1, 'a', 2n)).toEqual([1, 'a', 2n]);
});
