using System.Text;
using System.Text.Json;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.IRacing.Win32;

namespace PaceLaps.Agent.Hosting;

/// <summary>
/// The agent's credentials from pairing, encrypted for the current Windows user (DPAPI). One server per file; the
/// token never appears in logs or on screen.
/// </summary>
public sealed class CredentialStore(string path)
{
    public static string DefaultPath { get; } = AgentPaths.File("credentials.bin");

    public string FilePath { get; } = path;

    public void Save(AgentCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(new Stored(credentials.Server.ToString(), credentials.AgentId, credentials.Token));
        File.WriteAllBytes(FilePath, Dpapi.Protect(json));
        Array.Clear(json);
    }

    /// <summary>The stored credentials, or null if there are none (or they belong to another Windows user).</summary>
    public AgentCredentials? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(FilePath))));
            return stored is null ? null : new AgentCredentials(new Uri(stored.Server), stored.AgentId, stored.Token);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or JsonException or UriFormatException)
        {
            return null;
        }
    }

    private sealed record Stored(string Server, string AgentId, string Token);
}
