using IndustrialDataSim.Cli;

using var stop = new CancellationTokenSource();
ConsoleCancelEventHandler requestStop = (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.CancelKeyPress += requestStop;
try { return CliApplication.Run(args, Console.Out, stop.Token); }
finally { Console.CancelKeyPress -= requestStop; }
