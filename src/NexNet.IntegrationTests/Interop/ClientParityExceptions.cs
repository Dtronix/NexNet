namespace NexNet.IntegrationTests.Interop;

/// <summary>The client languages of a parity exception: Java, Node, and the browsers (Chromium, Firefox, WebKit).</summary>
[Flags]
internal enum ParityLanguages
{
    /// <summary>No language.</summary>
    None = 0,

    /// <summary>The Java client.</summary>
    Java = 1,

    /// <summary>The JavaScript client on Node.</summary>
    Node = 2,

    /// <summary>The JavaScript client in every browser.</summary>
    Browser = 4,

    /// <summary>Every language but C#.</summary>
    All = Java | Node | Browser,
}

/// <summary>How a matrix test falls short of parity on a language (<see cref="ClientParityTests"/>).</summary>
internal enum ParityGap
{
    /// <summary>
    /// No row of the language runs: its transports or capabilities rule out every row, the attribute's
    /// <c>Languages</c> leaves it out, or the test skips it at run time (<c>MatrixTestBase.RequireInProc</c>).
    /// </summary>
    NoRunnableRow,

    /// <summary>
    /// The rows run, but part of the test checks the C# client only (a branch on <c>ClientLanguages.CSharp</c>, or
    /// <c>MatrixTestBase.InProc</c>). <c>MatrixTestBase.AssertNotOutstanding</c> checks every language and is not a gap.
    /// </summary>
    CSharpOnlyAssertion,

    /// <summary>
    /// The rows run, but the test branches on the language and checks something else, often less, on it: a string
    /// comparison against a <c>ClientLanguages</c> name or family (<c>target.Language.StartsWith("browser-")</c>), in the
    /// test or a helper it calls. The reason says what the platform cannot do.
    /// </summary>
    PlatformVariantAssertion,
}

/// <summary>A documented parity exception: a matrix test that does not run, or not fully, on some languages.</summary>
/// <param name="Test">The test, as <c>Class.Method</c>.</param>
/// <param name="Gap">What is missing.</param>
/// <param name="Languages">The languages it is missing on.</param>
/// <param name="Reason">Why. Platform limits and by-design gaps say so; a gap a card will close names the card.</param>
internal sealed record ParityException(string Test, ParityGap Gap, ParityLanguages Languages, string Reason);

/// <summary>
/// Every matrix test that does not run, or not fully, on every client language, and why. <see cref="ClientParityTests"/>
/// fails when a test has a gap that is not listed here, and when an entry here no longer matches a gap.
/// </summary>
/// <remarks>
/// Transports and capabilities per language: <c>src/NexNet.Interop/ClientLanguages.cs</c>. Java has no UDS and nothing
/// but C# has QUIC, so a test that lists another transport as well is not a gap; a test that lists only those is.
/// </remarks>
internal static class ClientParityExceptions
{
    /// <summary>The entry <c>ClientParityTests.Audit_FindsAMissingRow</c> removes to check the audit's message.</summary>
    internal const string Probe = "AspSessionUserTests.NonAspTransport_SessionUserIsNull";

    // ----- Reasons -----

    /// <summary>Browsers: WebSocket and WSS only, and those are ASP.NET endpoints.</summary>
    private const string BrowserNonAspTransport =
        "Platform limit: the test needs a non-ASP.NET transport (UDS, TCP, TLS), and a browser has only WebSocket/WSS, which are ASP.NET endpoints.";

    private const string BrowserAuthorizationHeader =
        "Platform limit: the browser WebSocket API cannot set an Authorization header. The query-string token tests in AspClientServerTests cover the replacement on the browser.";

    private const string BrowserUpgradeHeader =
        "Platform limit: the browser WebSocket API cannot set request headers. The query-string token tests in AspClientServerTests cover the replacement on the browser.";

    private const string BrowserHttpSocket =
        "Platform limit: a browser has no HttpSocket transport (page JS cannot take over an HTTP connection). The WebSocket rows of ClientTransportConformanceTests.UpgradeRedirect_IsNotFollowed_FailsWithProtocolError cover the browser's upgrade handling.";

    private const string BrowserTls =
        "Platform limit: page JS has no custom CA, client certificate, pinning or server-certificate access; the browser's TLS stack does it. The browser WSS rows prove the default-trust path.";

    private const string CopyTo =
        "By design: ICollection<T>.CopyTo of the C# INexusList. JS has toArray() and Java a snapshot; no equivalent is needed.";

    private const string ServerPipeObject =
        "By design: only C# can hold a server-side pipe object, so only the C# client can pass one from the wrong nexus.";

    private const string ExceptionTypeName =
        "By design: exception type names are language-specific; the other languages check the error kind and message.";

    private const string BrowserRedirectError =
        "Platform limit: the browser WebSocket API reports a refused upgrade (a redirect included) only as a generic error event, with no status, so the browser client cannot tell a protocol error from a transport failure and reports InternalError, not ProtocolError. The rest of the test (the redirect is not followed, one request) holds on every language.";

    private const string BrowserCloseCode =
        "Platform limit: page JS may only send close code 1000 or 3000-4999 (WebSocket.close throws InvalidAccessError for 1003), so a browser client closes normally (1000) on a text message. That it refuses the session holds on every language.";

    private const string BrowserRemoteEndpoint =
        "Platform limit: the browser does not expose the peer address of a WebSocket, so a browser client reports RemoteEndpoint.None while connected. The empty endpoint before connect and after disconnect is checked on every language.";

    private const string BrowserDefaultReconnection =
        "By design: only the browser client turns on its default reconnection policy by default (lifecycle reconnect, D16); C#, Node and Java have none. The browser rows check that it reconnects, the others that it does not.";

    private const string BrowserOrigin =
        "By design: only a browser sends its page's Origin header, so only the browser rows exercise the allow-list (refused, then allowed once its origin is added); the other clients send no Origin and are always allowed.";

    private const string JavaMicrometer =
        "By design: the Java rows record through the Java client's Micrometer module instead of its native recorder, to cover that module; the assertions are the same for both.";

    private const string ListTestCounter =
        "Runtime internal: only the count of changes a list received before its snapshot is checked on C# alone. It is a C# runtime test field (NexusListClient.TestChangesReceivedBeforeSnapshot) that no harness operation exposes. The list state is asserted on every language, and the other languages are checked by their final contents, which a change applied before the snapshot would corrupt.";

    /// <summary>The exceptions.</summary>
    internal static readonly ParityException[] All =
    [
        // Browser platform limits (A).
        new(Probe, ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserNonAspTransport),
        new("AspClientServerTests.ClientAuthenticationSucceeds", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserAuthorizationHeader),
        new("AspClientServerTests.ClientAuthenticationFailsWithBadAuth", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserAuthorizationHeader),
        new("AspClientServerTests.ServerIgnoresAuthenticationWhenNotEnabled", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserAuthorizationHeader),
        new("AspClientServerTests.ClientDoesNotConnectWhenServerSelectsBadAuthScheme", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserAuthorizationHeader),
        new("AspClientServerTests.AuthorizationHeaderWinsOverQueryStringToken", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserAuthorizationHeader),
        new("AspClientServerTests.CustomUpgradeHeaderReachesTheServer", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserUpgradeHeader),
        new("AspSessionUserTests.OnAuthenticate_SeesAspPrincipalClaims", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserAuthorizationHeader),
        new("AspSessionUserTests.NexusAuthenticationStillDecides_WithAspPrincipal", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserAuthorizationHeader),
        new("TlsCertificateTests.TrustedTestCa_AcceptsServerCertificate", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserTls),
        new("TlsCertificateTests.TrustedTestCa_RejectsWrongHostName", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserTls),
        new("TlsCertificateTests.NoClientCertificate_ServerSessionHasNone", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserTls),
        new("TlsCertificateTests.UntrustedServerCertificate_RejectedWithoutCallback", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserTls),
        new("TlsCertificateTests.ClientCertificate_ReachesServerSession", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserTls),
        new("TlsCertificateTests.ClientSession_SeesServerCertificate", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserTls),
        new("TlsCertificateTests.PinnedFingerprint_AcceptsServerCertificate", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserTls),
        new("TlsCertificateTests.PinnedFingerprint_RejectsWrongCertificate", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserTls),
        new("ClientSpecOptionsTests.InsecureSkipVerify_AcceptsACertificateTheClientDoesNotTrust", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserTls),
        new("ClientTransportConformanceTests.HttpSocket_SuccessStatusOtherThan101_FailsConnect", ParityGap.NoRunnableRow, ParityLanguages.Browser, BrowserHttpSocket),

        // Browser platform limits: weaker assertions on the browser rows (A).
        new("ClientTransportConformanceTests.UpgradeRedirect_IsNotFollowed_FailsWithProtocolError", ParityGap.PlatformVariantAssertion, ParityLanguages.Browser, BrowserRedirectError),
        new("ClientTransportConformanceTests.WebSocket_TextMessage_ClientClosesWith1003", ParityGap.PlatformVariantAssertion, ParityLanguages.Browser, BrowserCloseCode),
        new("ClientSessionInfoTests.RemoteEndpoint_IsTheServerEndpointWhileConnected", ParityGap.PlatformVariantAssertion, ParityLanguages.Browser, BrowserRemoteEndpoint),

        // C# API by design (D).
        new("NexusListTests.CopyTo_CopiesAllElements", ParityGap.NoRunnableRow, ParityLanguages.All, CopyTo),
        new("NexusListTests.CopyTo_NullArray_ThrowsArgumentNullException", ParityGap.NoRunnableRow, ParityLanguages.All, CopyTo),
        new("NexusListTests.CopyTo_SmallArray_ThrowsArgumentException", ParityGap.NoRunnableRow, ParityLanguages.All, CopyTo),
        new("NexusServerTests_NexusDuplexPipe.Server_ThrowsWhenPassingPipeFromWrongNexus", ParityGap.NoRunnableRow, ParityLanguages.All, ServerPipeObject),
        new("NexusClientTests_NexusDuplexPipe.Client_ThrowsWhenPassingPipeFromWrongNexus", ParityGap.CSharpOnlyAssertion, ParityLanguages.All, ServerPipeObject),
        new("RemoteExceptionDetailTests.TypeAndMessage_ServerCallingClient_CarriesTypeAndMessage", ParityGap.CSharpOnlyAssertion, ParityLanguages.All, ExceptionTypeName),
        new("NexusCollectionSnapshotOrderTests.ClientsJoiningMidStream_ReceiveSnapshotBeforeChanges", ParityGap.CSharpOnlyAssertion, ParityLanguages.All, ListTestCounter),

        // Language variants by design (D).
        new("ReconnectionPolicyTests.DefaultReconnection_OnlyABrowserClientReconnects", ParityGap.PlatformVariantAssertion, ParityLanguages.Browser, BrowserDefaultReconnection),
        new("CrossLanguageSmokeTests.Origin_NotInTheAllowList_RefusesOnlyABrowser", ParityGap.PlatformVariantAssertion, ParityLanguages.Browser, BrowserOrigin),
        new("CrossLanguageSmokeTests.Origin_InTheAllowList_Connects", ParityGap.PlatformVariantAssertion, ParityLanguages.Browser, BrowserOrigin),
        new("ClientMetricsTests.SessionLifecycle_Measured_AlsoThroughMicrometer", ParityGap.PlatformVariantAssertion, ParityLanguages.Java, JavaMicrometer),
    ];
}
