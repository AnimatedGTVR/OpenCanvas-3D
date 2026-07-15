using Godot;
using System.Collections.Generic;

namespace OpenCanvas3D;

// Persists small cross-session app state (theme choice, first-launch flag,
// recent project files) to one user://settings.cfg ConfigFile, and carries
// transient (never persisted) "what should the next scene do" handoff state
// across ChangeSceneToFile calls — Godot destroys the old scene tree on a
// scene change, so an autoload is the standard place to stash state that
// needs to survive that boundary.
public partial class AppSettings : Node
{
    private const string SettingsPath = "user://settings.cfg";
    private const int MaxRecentFiles = 10;

    public bool DarkTheme { get; set; }
    public bool FirstLaunch { get; set; } = true;
    public readonly List<(string Path, string OpenedAt)> RecentFiles = new();

    // Transient handoff, set by StartScreen before a scene change, consumed
    // once by PaintController._Ready() — never written to disk.
    public string? PendingProjectPath;
    public string? PendingImportPath;

    public override void _Ready()
    {
        Load();
    }

    public void Load()
    {
        var config = new ConfigFile();
        if (config.Load(SettingsPath) != Error.Ok)
            return;

        DarkTheme = (bool)config.GetValue("ui", "theme_dark", false);
        FirstLaunch = (bool)config.GetValue("app", "first_launch", true);

        RecentFiles.Clear();
        int count = (int)config.GetValue("recent", "count", 0);
        for (int i = 0; i < count; i++)
        {
            var path = (string)config.GetValue("recent", $"path_{i}", "");
            var openedAt = (string)config.GetValue("recent", $"opened_{i}", "");
            if (!string.IsNullOrEmpty(path))
                RecentFiles.Add((path, openedAt));
        }
    }

    public void Save()
    {
        var config = new ConfigFile();
        config.SetValue("ui", "theme_dark", DarkTheme);
        config.SetValue("app", "first_launch", FirstLaunch);

        config.SetValue("recent", "count", RecentFiles.Count);
        for (int i = 0; i < RecentFiles.Count; i++)
        {
            config.SetValue("recent", $"path_{i}", RecentFiles[i].Path);
            config.SetValue("recent", $"opened_{i}", RecentFiles[i].OpenedAt);
        }

        config.Save(SettingsPath);
    }

    public void AddRecentFile(string path)
    {
        RecentFiles.RemoveAll(entry => entry.Path == path);
        RecentFiles.Insert(0, (path, Time.GetDatetimeStringFromSystem()));
        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        Save();
    }
}
