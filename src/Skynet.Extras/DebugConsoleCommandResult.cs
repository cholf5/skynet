namespace Skynet.Extras;

/// <summary>
/// Represents the outcome of executing a console command.
/// </summary>
public sealed record DebugConsoleCommandResult(string Response, bool ShouldExit);
