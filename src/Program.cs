using MsTeamsLocal;

// Special mode: regenerate the static manifest icon files, then exit.
if (args.Length >= 1 && args[0] == "--emit-icons")
{
    var outDir = args.Length >= 2 ? args[1] : AppContext.BaseDirectory;
    IconEmitter.EmitAll(outDir);
    Console.WriteLine($"icons written to {outDir}");
    return;
}

// Stream Deck launches the plugin with: -port N -pluginUUID X -registerEvent Y -info {json}
var argMap = ParseArgs(args);
if (!argMap.TryGetValue("-port", out var portStr) ||
    !int.TryParse(portStr, out var port) ||
    !argMap.TryGetValue("-pluginUUID", out var pluginUuid) ||
    !argMap.TryGetValue("-registerEvent", out var registerEvent))
{
    Log.Error("missing Stream Deck registration arguments; exiting");
    return;
}

Log.Info("msteams-local starting");

using var cts = new CancellationTokenSource();
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var teams = new TeamsAutomation();
using var connection = new StreamDeckConnection(port, pluginUuid, registerEvent);
_ = new PluginHost(connection, teams);

try
{
    await connection.ConnectAndRegisterAsync(cts.Token);
    teams.Start();
    await connection.ReceiveLoopAsync(cts.Token);
}
catch (OperationCanceledException) { /* shutting down */ }
catch (Exception ex)
{
    Log.Error("fatal", ex);
}
finally
{
    teams.Dispose();
}

Log.Info("msteams-local exiting");
return;

static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i + 1 < args.Length; i += 2)
        map[args[i]] = args[i + 1];
    return map;
}
