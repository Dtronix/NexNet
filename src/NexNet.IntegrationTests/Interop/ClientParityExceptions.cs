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

    private const string BrowserTls =
        "Platform limit: page JS has no custom CA, client certificate, pinning or server-certificate access; the browser's TLS stack does it. The browser WSS rows prove the default-trust path.";

    private const string CopyTo =
        "By design: ICollection<T>.CopyTo of the C# INexusList. JS has toArray() and Java a snapshot; no equivalent is needed.";

    private const string ServerPipeObject =
        "By design: only C# can hold a server-side pipe object, so only the C# client can pass one from the wrong nexus.";

    private const string ExceptionTypeName =
        "By design: exception type names are language-specific; the other languages check the error kind and message.";

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

        // C# API by design (D).
        new("NexusListTests.CopyTo_CopiesAllElements", ParityGap.NoRunnableRow, ParityLanguages.All, CopyTo),
        new("NexusListTests.CopyTo_NullArray_ThrowsArgumentNullException", ParityGap.NoRunnableRow, ParityLanguages.All, CopyTo),
        new("NexusListTests.CopyTo_SmallArray_ThrowsArgumentException", ParityGap.NoRunnableRow, ParityLanguages.All, CopyTo),
        new("NexusServerTests_NexusDuplexPipe.Server_ThrowsWhenPassingPipeFromWrongNexus", ParityGap.NoRunnableRow, ParityLanguages.All, ServerPipeObject),
        new("NexusClientTests_NexusDuplexPipe.Client_ThrowsWhenPassingPipeFromWrongNexus", ParityGap.CSharpOnlyAssertion, ParityLanguages.All, ServerPipeObject),
        new("RemoteExceptionDetailTests.TypeAndMessage_ServerCallingClient_CarriesTypeAndMessage", ParityGap.CSharpOnlyAssertion, ParityLanguages.All, ExceptionTypeName),
    ];
}
