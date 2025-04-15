using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NexNet.Internals;

internal static unsafe class EnumUtilities<T>
    where T : unmanaged, Enum
{
    public static readonly int Size = sizeof(T);
    /// <summary>
    /// Sets the flag enum with a interlocked operation.
    /// </summary>
    /// <param name="field">Field of the enum to set.</param>
    /// <param name="additionalFlag">Additional flag to set on the enum.</param>
    /// <returns>The previous value in the Field.</returns>
    /// <remarks>
    /// Method will attempt to set the enum a total of 50 times.  If it fails to set the value, method throws.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T SetFlag(ref T field, T additionalFlag)

    {
        const int maxLoops = 50;

        switch (Size)
        {
            case 1:
            {
                var initialValue = Unsafe.BitCast<T, byte>(field);

                if (Interlocked.CompareExchange(
                        ref Unsafe.As<T, byte>(ref field),
                        (byte)(Unsafe.BitCast<T, byte>(additionalFlag) | initialValue),
                        Unsafe.BitCast<T, byte>(field)) == initialValue)
                    return Unsafe.BitCast<byte, T>(initialValue);
                // Attempt to set the enum a maximum number of times.
                for (int i = 0; i < maxLoops; i++)
                {
                    Thread.Yield();
                    initialValue = Unsafe.BitCast<T, byte>(field);

                    if (Interlocked.CompareExchange(
                            ref Unsafe.As<T, byte>(ref field),
                            (byte)(Unsafe.BitCast<T, byte>(additionalFlag) | initialValue),
                            Unsafe.BitCast<T, byte>(field)) == initialValue)
                        return Unsafe.BitCast<byte, T>(initialValue);
                }

                break;
            }
            case 2:
            {
                var initialValue = Unsafe.BitCast<T, ushort>(field);

                if (Interlocked.CompareExchange(
                        ref Unsafe.As<T, ushort>(ref field),
                        (ushort)(Unsafe.BitCast<T, ushort>(additionalFlag) | initialValue),
                        Unsafe.BitCast<T, ushort>(field)) == initialValue)
                    return Unsafe.BitCast<ushort, T>(initialValue);
                
                // Attempt to set the enum a maximum number of times.
                for (int i = 0; i < maxLoops; i++)
                {
                    Thread.Yield();
                    initialValue = Unsafe.BitCast<T, ushort>(field);

                    if (Interlocked.CompareExchange(
                            ref Unsafe.As<T, ushort>(ref field),
                            (ushort)(Unsafe.BitCast<T, ushort>(additionalFlag) | initialValue),
                            Unsafe.BitCast<T, ushort>(field)) == initialValue)
                        return Unsafe.BitCast<ushort, T>(initialValue);
                }

                break;
            }
            case 4:
            {
                var initialValue = Unsafe.BitCast<T, int>(field);

                if (Interlocked.CompareExchange(
                        ref Unsafe.As<T, int>(ref field),
                        Unsafe.BitCast<T, int>(additionalFlag) | initialValue,
                        Unsafe.BitCast<T, int>(field)) == initialValue)
                    return Unsafe.BitCast<int, T>(initialValue);
                
                // Attempt to set the enum a maximum number of times.
                for (int i = 0; i < maxLoops; i++)
                {
                    Thread.Yield();
                    initialValue = Unsafe.BitCast<T, int>(field);

                    if (Interlocked.CompareExchange(
                            ref Unsafe.As<T, int>(ref field),
                            Unsafe.BitCast<T, int>(additionalFlag) | initialValue,
                            Unsafe.BitCast<T, int>(field)) == initialValue)
                        return Unsafe.BitCast<int, T>(initialValue);
                }

                break;
            }
            case 8:
            {
                var initialValue = Unsafe.BitCast<T, long>(field);

                if (Interlocked.CompareExchange(
                        ref Unsafe.As<T, long>(ref field),
                        Unsafe.BitCast<T, long>(additionalFlag) | initialValue,
                        Unsafe.BitCast<T, long>(field)) == initialValue)
                    return Unsafe.BitCast<long, T>(initialValue);
                
                // Attempt to set the enum a maximum number of times.
                for (int i = 0; i < maxLoops; i++)
                {
                    Thread.Yield();
                    initialValue = Unsafe.BitCast<T, long>(field);

                    if (Interlocked.CompareExchange(
                            ref Unsafe.As<T, long>(ref field),
                            Unsafe.BitCast<T, long>(additionalFlag) | initialValue,
                            Unsafe.BitCast<T, long>(field)) == initialValue)
                        return Unsafe.BitCast<long, T>(initialValue);
                }

                break;
            }
            default:
                throw new Exception($"Unknown enum {typeof(T)}.");
        }

        throw new Exception($"Could not change the enum {typeof(T).Name} flag to include the {additionalFlag} flag.");
    }
    
    /// <summary>
    /// Sets the flag enum with a interlocked operation.
    /// </summary>
    /// <param name="field">Field of the enum to set.</param>
    /// <param name="additionalFlag">Additional flag to set on the enum.</param>
    /// <returns>The previous value in the Field.</returns>
    /// <remarks>
    /// Method will attempt to set the enum a total of 50 times.  If it fails to set the value, method throws.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T RemoveFlag(ref T field, T additionalFlag)

    {
        const int maxLoops = 50;
        switch (Size)
        {
            case 1:
            {
                var initialValue = Unsafe.BitCast<T, byte>(field);

                if (Interlocked.CompareExchange(
                        ref Unsafe.As<T, byte>(ref field),
                        (byte)(~Unsafe.BitCast<T, byte>(additionalFlag) & initialValue),
                        Unsafe.BitCast<T, byte>(field)) == initialValue)
                    return Unsafe.BitCast<byte, T>(initialValue);
                // Attempt to set the enum a maximum number of times.
                for (int i = 0; i < maxLoops; i++)
                {
                    Thread.Yield();
                    initialValue = Unsafe.BitCast<T, byte>(field);

                    if (Interlocked.CompareExchange(
                            ref Unsafe.As<T, byte>(ref field),
                            (byte)(~Unsafe.BitCast<T, byte>(additionalFlag) & initialValue),
                            Unsafe.BitCast<T, byte>(field)) == initialValue)
                        return Unsafe.BitCast<byte, T>(initialValue);
                }

                break;
            }
            case 2:
            {
                var initialValue = Unsafe.BitCast<T, ushort>(field);

                if (Interlocked.CompareExchange(
                        ref Unsafe.As<T, ushort>(ref field),
                        (ushort)(~Unsafe.BitCast<T, ushort>(additionalFlag) & initialValue),
                        Unsafe.BitCast<T, ushort>(field)) == initialValue)
                    return Unsafe.BitCast<ushort, T>(initialValue);
                // Attempt to set the enum a maximum number of times.
                for (int i = 0; i < maxLoops; i++)
                {
                    Thread.Yield();
                    initialValue = Unsafe.BitCast<T, ushort>(field);

                    if (Interlocked.CompareExchange(
                            ref Unsafe.As<T, ushort>(ref field),
                            (ushort)(~Unsafe.BitCast<T, ushort>(additionalFlag) & initialValue),
                            Unsafe.BitCast<T, ushort>(field)) == initialValue)
                        return Unsafe.BitCast<ushort, T>(initialValue);
                }

                break;
            }
            case 4:
            {
                var initialValue = Unsafe.BitCast<T, int>(field);

                if (Interlocked.CompareExchange(
                        ref Unsafe.As<T, int>(ref field),
                        ~Unsafe.BitCast<T, int>(additionalFlag) & initialValue,
                        Unsafe.BitCast<T, int>(field)) == initialValue)
                    return Unsafe.BitCast<int, T>(initialValue);
                // Attempt to set the enum a maximum number of times.
                for (int i = 0; i < maxLoops; i++)
                {
                    Thread.Yield();
                    initialValue = Unsafe.BitCast<T, int>(field);

                    if (Interlocked.CompareExchange(
                            ref Unsafe.As<T, int>(ref field),
                            ~Unsafe.BitCast<T, int>(additionalFlag) & initialValue,
                            Unsafe.BitCast<T, int>(field)) == initialValue)
                        return Unsafe.BitCast<int, T>(initialValue);
                }

                break;
            }
            case 8:
            {
                var initialValue = Unsafe.BitCast<T, long>(field);

                if (Interlocked.CompareExchange(
                        ref Unsafe.As<T, long>(ref field),
                        ~Unsafe.BitCast<T, long>(additionalFlag) & initialValue,
                        Unsafe.BitCast<T, long>(field)) == initialValue)
                    return Unsafe.BitCast<long, T>(initialValue);
                
                // Attempt to set the enum a maximum number of times.
                for (int i = 0; i < maxLoops; i++)
                {
                    Thread.Yield();
                    initialValue = Unsafe.BitCast<T, long>(field);

                    if (Interlocked.CompareExchange(
                            ref Unsafe.As<T, long>(ref field),
                            ~Unsafe.BitCast<T, long>(additionalFlag) & initialValue,
                            Unsafe.BitCast<T, long>(field)) == initialValue)
                        return Unsafe.BitCast<long, T>(initialValue);
                }

                break;
            }
            default:
                throw new Exception($"Unknown enum {typeof(T)}.");
        }

        throw new Exception($"Could not change the enum {typeof(T).Name} flag to exclude the {additionalFlag} flag.");
    }
}
