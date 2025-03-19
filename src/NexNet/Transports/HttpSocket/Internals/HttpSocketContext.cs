// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Security.Principal;

namespace NexNet.Transports.HttpSocket.Internals;

public abstract class HttpSocketContext
{
    public abstract Uri RequestUri { get; }
    public abstract NameValueCollection Headers { get; }
    public abstract string Origin { get; }
    public abstract IEnumerable<string> SecHttpSocketProtocols { get; }
    public abstract string SecHttpSocketVersion { get; }
    public abstract string SecHttpSocketKey { get; }
    public abstract CookieCollection CookieCollection { get; }
    public abstract IPrincipal? User { get; }
    public abstract bool IsAuthenticated { get; }
    public abstract bool IsLocal { get; }
    public abstract bool IsSecureConnection { get; }
    public abstract HttpSocket HttpSocket { get; }
}