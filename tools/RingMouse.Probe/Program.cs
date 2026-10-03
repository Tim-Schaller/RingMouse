using System.Text;
using RingMouse.Core.Config;
using RingMouse.Core.Localization;
using RingMouse.HidPlusPlus;
using RingMouse.Probe;

Console.OutputEncoding = Encoding.UTF8;
// Entwicklerwerkzeug: immer englisch, auch die Zustandstexte aus dem DeviceService (watch)
Lang.Apply(UiLanguage.English);

ProbeOptions options;
try
{
    options = ProbeOptions.Parse(args);
}
catch (ProbeException ex)
{
    ConsoleOut.Error(ex.Message);
    Console.WriteLine();
    Console.WriteLine(ProbeOptions.Usage);
    return 2;
}

if (options.Command is "help")
{
    Console.WriteLine(ProbeOptions.Usage);
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    if (options.Command == "list")
        return await Commands.ListAsync(options, cts.Token);
    if (options.Command == "watch")
        return await WatchCommand.RunAsync(options, cts.Token);

    Func<ProbeSession, CancellationToken, Task<int>> command = options.Command switch
    {
        "info" => Commands.InfoAsync,
        "features" => Commands.FeaturesAsync,
        "controls" => Commands.ControlsAsync,
        "battery" => Commands.BatteryAsync,
        "dpi" => Commands.DpiAsync,
        "live" => Commands.LiveAsync,
        "monitor" => Commands.MonitorAsync,
        "reset" => Commands.ResetAsync,
        "dump" => Commands.DumpAsync,
        _ => throw new ProbeException($"Unknown command: {options.Command} (see ringmouse-probe --help)"),
    };

    using var session = await ProbeSession.OpenAsync(options, cts.Token);
    return await command(session, cts.Token);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (ProbeException ex)
{
    ConsoleOut.Error(ex.Message);
    return 2;
}
catch (HidppTimeoutException ex)
{
    ConsoleOut.Error(ex.Message);
    ConsoleOut.Hint("Tip: the mouse may be asleep – move it briefly and try again, or increase --timeout.");
    return 3;
}
catch (HidppException ex)
{
    ConsoleOut.Error(ex.Message);
    return 3;
}
catch (Exception ex)
{
    ConsoleOut.Error(ex.ToString());
    return 1;
}
