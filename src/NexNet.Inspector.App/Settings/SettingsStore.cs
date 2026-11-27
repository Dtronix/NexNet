using System.Text.Json;

namespace NexNet.Inspector.App.Settings;

/// <summary>How <see cref="SettingsStore.Load"/> went.</summary>
public enum SettingsLoadOutcome : byte
{
    /// <summary>No file yet: defaults.</summary>
    Missing = 0,

    /// <summary>The file was read.</summary>
    Loaded = 1,

    /// <summary>The file was not valid settings JSON: it was copied to <c>settings.json.bak</c> and defaults are used.</summary>
    Corrupt = 2,

    /// <summary>The file could not be read (locked, no permission): defaults are used and the file is left alone.</summary>
    Unreadable = 3,
}

/// <summary>
/// Reads and writes one <c>settings.json</c>. A save is atomic (a temp file, flushed, then moved over the old one), so a
/// crash mid-write leaves the previous file whole. A corrupt file never stops the app: it is kept as <c>.bak</c> for
/// the user and the app starts on defaults.
/// </summary>
public sealed class SettingsStore(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>Where a corrupt file is kept.</summary>
    public string BackupPath => Path + ".bak";

    private string TempPath => Path + ".tmp";

    /// <summary>The settings as last loaded or saved.</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>Loads the file into <see cref="Current"/>.</summary>
    public SettingsLoadOutcome Load()
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(Path))
            {
                Current = new AppSettings();
                return SettingsLoadOutcome.Missing;
            }

            bytes = File.ReadAllBytes(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Current = new AppSettings();
            return SettingsLoadOutcome.Unreadable;
        }

        AppSettings? read;
        try
        {
            read = JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.AppSettings);
        }
        catch (JsonException)
        {
            read = null;
        }

        if (read is null)
        {
            KeepBackup();
            Current = new AppSettings();
            return SettingsLoadOutcome.Corrupt;
        }

        Current = read.Normalized();
        return SettingsLoadOutcome.Loaded;
    }

    /// <summary>Applies <paramref name="change"/> to <see cref="Current"/> and saves; false when the write failed.</summary>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Save(change(Current));
    }

    /// <summary>
    /// Makes <paramref name="settings"/> current and writes them atomically. False when the folder or file cannot be
    /// written: the settings still apply for this run, and the app carries on.
    /// </summary>
    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings);
            using (FileStream stream = new(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                // On disk before the move, or a power cut could leave the renamed file empty.
                stream.Flush(flushToDisk: true);
            }

            File.Move(TempPath, Path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(TempPath);
            return false;
        }
    }

    private void KeepBackup()
    {
        try
        {
            File.Copy(Path, BackupPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The backup is a courtesy: without it the app still starts on defaults.
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A stray temp file is overwritten by the next save.
        }
    }
}
