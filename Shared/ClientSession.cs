namespace RulesetUpdater;

// Shared as source so the bridge does not need another DLL beside it.
public sealed record ClientSession
{
    // Setters retain omitted-field defaults with .NET 8 source-generated JSON.
    public int Schema { get; set; } = 1;
    public int ProcessId { get; init; }
    public long ProcessStartUtcTicks { get; init; }
    public string Version { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public bool OfficialBuild { get; init; }
}
