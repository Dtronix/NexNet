namespace NexNet.Inspector.App.Settings;

/// <summary>Which settings-folder convention applies.</summary>
public enum SettingsPlatform : byte
{
    /// <summary><c>%LOCALAPPDATA%\NexNetInspector</c>.</summary>
    Windows = 0,

    /// <summary><c>$XDG_CONFIG_HOME/nexnet-inspector</c>, else <c>~/.config/nexnet-inspector</c> (Linux, and any other Unix).</summary>
    Unix = 1,
}

/// <summary>Where the per-user <c>settings.json</c> lives.</summary>
public static class SettingsPaths
{
    public const string FileName = "settings.json";

    /// <summary>The settings file for this user on this OS.</summary>
    public static string File() => Path.Combine(Directory(), FileName);

    /// <summary>The settings folder for this user on this OS.</summary>
    public static string Directory() => OperatingSystem.IsWindows()
        ? Directory(SettingsPlatform.Windows, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), null, null)
        : Directory(SettingsPlatform.Unix, null, Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// The settings folder from the environment values given, joined with the target OS's separator so the rule can be
    /// tested for either OS on any OS.
    /// </summary>
    public static string Directory(SettingsPlatform platform, string? localAppData, string? xdgConfigHome, string? home)
    {
        if (platform == SettingsPlatform.Windows)
        {
            if (string.IsNullOrEmpty(localAppData)) throw new InvalidOperationException("LOCALAPPDATA is not set: no folder for the settings");
            return localAppData.TrimEnd('\\', '/') + "\\NexNetInspector";
        }

        // The XDG Base Directory spec says a relative XDG_CONFIG_HOME is invalid and must be ignored.
        if (!string.IsNullOrEmpty(xdgConfigHome) && xdgConfigHome[0] == '/')
            return xdgConfigHome.TrimEnd('/') + "/nexnet-inspector";
        if (string.IsNullOrEmpty(home)) throw new InvalidOperationException("neither XDG_CONFIG_HOME nor HOME is set: no folder for the settings");
        return home.TrimEnd('/') + "/.config/nexnet-inspector";
    }
}
