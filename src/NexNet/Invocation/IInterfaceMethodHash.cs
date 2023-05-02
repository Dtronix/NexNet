﻿namespace NexNet.Invocation;

public interface IInterfaceMethodHash
{
    /// <summary>
    /// This is a hash based upon the interface's method names, arguments and return values.
    /// Used to ensure the other connection is in sync with this connection's hub.
    /// </summary>
    // ReSharper disable once UnassignedGetOnlyAutoProperty
    public static abstract int MethodHash { get; }
}
