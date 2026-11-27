using NexNet.Inspector.App.Settings;

namespace NexNet.Inspector.App.Tests.Settings;

/// <summary>The per-OS settings folder (OQ13), tested for both OSes on any OS.</summary>
public sealed class SettingsPathsTests
{
    [Test]
    public async Task Windows_uses_local_app_data()
    {
        await Assert.That(SettingsPaths.Directory(SettingsPlatform.Windows, @"C:\Users\u\AppData\Local", "/ignored", "/ignored"))
            .IsEqualTo(@"C:\Users\u\AppData\Local\NexNetInspector");
        await Assert.That(SettingsPaths.Directory(SettingsPlatform.Windows, @"C:\Users\u\AppData\Local\", null, null))
            .IsEqualTo(@"C:\Users\u\AppData\Local\NexNetInspector");
    }

    [Test]
    public async Task Unix_uses_xdg_config_home()
    {
        await Assert.That(SettingsPaths.Directory(SettingsPlatform.Unix, null, "/home/u/.cfg", "/home/u")).IsEqualTo("/home/u/.cfg/nexnet-inspector");
        await Assert.That(SettingsPaths.Directory(SettingsPlatform.Unix, null, "/home/u/.cfg/", "/home/u")).IsEqualTo("/home/u/.cfg/nexnet-inspector");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("relative/cfg")]
    public async Task Unix_falls_back_to_dot_config_when_xdg_is_unset_or_relative(string? xdg)
    {
        await Assert.That(SettingsPaths.Directory(SettingsPlatform.Unix, null, xdg, "/home/u")).IsEqualTo("/home/u/.config/nexnet-inspector");
    }

    [Test]
    public async Task No_base_folder_is_an_error()
    {
        await Assert.That(() => SettingsPaths.Directory(SettingsPlatform.Windows, null, null, null)).Throws<InvalidOperationException>();
        await Assert.That(() => SettingsPaths.Directory(SettingsPlatform.Unix, null, null, "")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task This_os_file_is_settings_json_in_the_app_folder()
    {
        string file = SettingsPaths.File();
        string folder = Path.GetFileName(Path.GetDirectoryName(file))!;

        await Assert.That(Path.GetFileName(file)).IsEqualTo("settings.json");
        await Assert.That(folder).IsEqualTo(OperatingSystem.IsWindows() ? "NexNetInspector" : "nexnet-inspector");
    }
}
