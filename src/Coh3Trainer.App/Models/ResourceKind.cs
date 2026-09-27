namespace Coh3Trainer.Models;

public enum ResourceKind
{
    Manpower,
    Fuel,
    Army,
    CommandPoints,
    VictoryPoints
}

public enum ResourceValueType
{
    Int32,
    Single
}

public static class ResourceKindExtensions
{
    public static string DisplayName(this ResourceKind kind) => kind switch
    {
        ResourceKind.Manpower => "MANPOWER",
        ResourceKind.Fuel => "FUEL",
        ResourceKind.Army => "MUNIÇÕES",
        ResourceKind.CommandPoints => "COMMAND POINTS",
        ResourceKind.VictoryPoints => "VICTORY POINTS DO TIME",
        _ => kind.ToString().ToUpperInvariant()
    };
}
