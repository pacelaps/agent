using Microsoft.Win32;

namespace PaceLaps.Agent.Hosting.Tray;

/// <summary>Start with Windows: opt-in, off by default. The agent still starts Sleeping, so nothing is sent until Activate.</summary>
public interface IAutoStart
{
    bool Enabled { get; }

    void Set(bool enabled);
}

/// <summary>A value under the current user's Run key (no administrator rights, nothing for other users).</summary>
public sealed class RunKeyAutoStart(string command, string valueName = RunKeyAutoStart.DefaultValueName, string keyPath = RunKeyAutoStart.RunKey) : IAutoStart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "PaceLaps Agent";

    public bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string value && string.Equals(value, command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (enabled)
        {
            key.SetValue(valueName, command);
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>This program, quoted (its path may have spaces).</summary>
    public static RunKeyAutoStart ForThisProgram() => new($"\"{Environment.ProcessPath}\"");
}
