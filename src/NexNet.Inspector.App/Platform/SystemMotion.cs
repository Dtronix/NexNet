using System.Runtime.InteropServices;

namespace NexNet.Inspector.App.Platform;

/// <summary>
/// The operating system's motion preference. Windows: "Show animations in Windows" (<c>SPI_GETCLIENTAREAANIMATION</c>,
/// through a source-generated P/Invoke, so Native AOT needs no marshalling stubs at run time). Elsewhere this is a no-op that
/// reports animations on: GNOME keeps its switch in GSettings (<c>org.gnome.desktop.interface enable-animations</c>), which
/// is only readable through libgio or a <c>gsettings</c> process, neither worth a dependency for a 160 ms ease.
/// A failed read also leaves animations on, the default.
/// </summary>
public static partial class SystemMotion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary>False when the user turned the OS's animations off.</summary>
    public static bool ShowsAnimations()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            int on = 1;
            return Interpret(SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref on, 0), on);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // No such call on this system (a trimmed-down Windows): animations stay on, the default.
            return true;
        }
    }

    /// <summary>
    /// True when the OS asks for reduced motion: animated changes (a lane's height, OQ12) then apply at once, and the rail
    /// and menus use their short 80 ms motion.
    /// </summary>
    public static bool ReducesMotion() => !ShowsAnimations();

    /// <summary>The answer a <c>SystemParametersInfo</c> read gives: a failed read leaves animations on.</summary>
    internal static bool Interpret(bool read, int value) => !read || value != 0;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}
