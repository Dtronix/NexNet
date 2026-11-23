using Avalonia.Platform;
using NexNet.Inspector.App.Services;

namespace NexNet.Inspector.App.Tests;

/// <summary>An OS whose light/dark preference a test switches.</summary>
internal sealed class FakeOsTheme(PlatformThemeVariant current = PlatformThemeVariant.Light) : IOsThemeSource
{
    public PlatformThemeVariant Current { get; private set; } = current;

    public event EventHandler? Changed;

    public int Subscribers => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>The user flips the OS theme: the platform reports new colour values.</summary>
    public void Switch(PlatformThemeVariant variant)
    {
        Current = variant;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
