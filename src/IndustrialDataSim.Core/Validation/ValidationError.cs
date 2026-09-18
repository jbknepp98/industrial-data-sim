namespace IndustrialDataSim.Core.Validation;

/// <summary>
/// A machine-readable failure. Codes and paths are the contract; messages explain
/// the rule without echoing potentially sensitive caller-supplied values.
/// </summary>
public sealed record ValidationError(string Code, string Path, string Message);
