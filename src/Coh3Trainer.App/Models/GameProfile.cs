namespace Coh3Trainer.Models;

public sealed class GameProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string ProcessName { get; set; } = "RelicCoH3";
    public string ExecutableVersion { get; set; } = string.Empty;
    public string ModuleName { get; set; } = "RelicCoH3.exe";
    public Dictionary<ResourceKind, ResourceLocator> Resources { get; set; } = new();
}

public sealed class ResourceLocator
{
    public string BaseOffset { get; set; } = "0x0";
    public List<string> PointerOffsets { get; set; } = new();
    public ResourceValueType ValueType { get; set; } = ResourceValueType.Single;
    public double Minimum { get; set; } = 0;
    public double Maximum { get; set; } = 10_000_000;
}
