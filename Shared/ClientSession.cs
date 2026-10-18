namespace RulesetUpdater;

// Shared as source so the bridge does not need another DLL beside it.
public sealed record ClientSession
{
    public int Schema { get; init; } = 1;
    public int ProcessId { get; init; }
    public long ProcessStartUtcTicks { get; init; }
    public string Version { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string AssemblyPath { get; init; } = "";
    public bool OfficialBuild { get; init; }
}
