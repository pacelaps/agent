using PaceLaps.Agent.ConsoleHost.Cli;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Core.Config;

namespace PaceLaps.Agent.ConsoleHost.Poc;

/// <summary>
/// <c>config</c>: where the settings file is and what it says. <c>config --init</c> writes one with the defaults (and
/// every setting listed) when there is none.
/// </summary>
internal static class ConfigCommand
{
    public static int Run(CliArgs args)
    {
        var store = new ConfigStore(args.Get("file") ?? ConfigStore.DefaultPath);
        if (args.Has("init"))
        {
            if (store.Exists)
            {
                Console.WriteLine($"{store.Path} exists; not changed.");
            }
            else
            {
                store.Save(new AgentConfig());
                Console.WriteLine($"Written: {store.Path}");
            }
        }

        if (!store.Exists)
        {
            Console.WriteLine($"No settings file at {store.Path}: the defaults apply. 'config --init' writes one.");
        }

        var config = store.Load();
        Console.WriteLine($"Settings ({store.Path}):");
        Console.WriteLine($"  server          {config.Server?.ToString() ?? "(none: run --server URL, or the stub server)"}");
        Console.WriteLine($"  league          {config.League?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none: hosted sessions need one)"}");
        Console.WriteLine($"  macros          {(config.Macros.Enabled ? "on" : "off")}{(config.Macros.Slots.Count == 0 ? "" : ", slots " + string.Join(", ", config.Macros.Slots.OrderBy(s => s.Key).Select(s => $"{s.Key}={s.Value}")))}");
        Console.WriteLine($"  local failsafe  {(config.LocalFailsafe.Enabled ? "on" : "off")}, after {config.LocalFailsafe.DelaySeconds} s without the server");
        Console.WriteLine($"  app.ini         {config.AppIni ?? "(default location)"}");
        return 0;
    }
}
