using Avalonia;
using Avalonia.Headless;

namespace NexNet.Inspector.App.Tests;

/// <summary>
/// The one Avalonia headless session every rendering test shares: the real <see cref="App"/> (its styles, tokens and
/// fonts), drawn by Skia rather than the headless drawing stub so frames hold real pixels. It has no desktop lifetime, so
/// the app starts nothing of its own. Tests that use it are <c>[NotInParallel("avalonia")]</c>: the theme is process-wide.
/// </summary>
internal static class HeadlessSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

    /// <summary>Runs <paramref name="body"/> on the session's UI thread.</summary>
    public static Task<T> Run<T>(Func<T> body) => Session.Value.Dispatch(body, CancellationToken.None);

    /// <summary>Runs <paramref name="body"/> on the session's UI thread.</summary>
    public static Task Run(Action body) => Session.Value.Dispatch(body, CancellationToken.None);

    /// <summary>The application the session hosts.</summary>
    public static Application App => Application.Current ?? throw new InvalidOperationException("the headless session has not started");
}

/// <summary>The session's entry point (found by name by <see cref="HeadlessUnitTestSession.StartNew(Type)"/>).</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHarfBuzz()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
