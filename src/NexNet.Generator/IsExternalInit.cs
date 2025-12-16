// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This file is used to enable init-only setters and record types in netstandard2.0
// See: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-9.0/init

#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    /// <summary>
    /// Reserved to be used by the compiler for tracking metadata.
    /// This class should not be used by developers in source code.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
#endif
