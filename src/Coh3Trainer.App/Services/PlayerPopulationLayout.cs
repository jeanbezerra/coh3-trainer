using Coh3Trainer.Models;

namespace Coh3Trainer.Services;

public static class PlayerPopulationLayout
{
    public const string SupportedGameVersion = "5.1.50313.0";
    public const int OverrideOffset = 0x50C;
    public const int OverrideSize = sizeof(float) * 3;

    public static bool IsSupportedVersion(string gameVersion) =>
        string.Equals(gameVersion, SupportedGameVersion, StringComparison.OrdinalIgnoreCase);

    public static byte[] CreateOverride(int personnelLimit)
    {
        if (!PopulationLimitRules.IsValidLimit(personnelLimit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(personnelLimit),
                $"The limit must be between {PopulationLimitRules.MinimumLimit} and {PopulationLimitRules.MaximumLimit}.");
        }

        var buffer = new byte[OverrideSize];
        BitConverter.GetBytes((float)personnelLimit).CopyTo(buffer, 0);
        BitConverter.GetBytes(float.MaxValue).CopyTo(buffer, sizeof(float));
        BitConverter.GetBytes(float.MaxValue).CopyTo(buffer, sizeof(float) * 2);
        return buffer;
    }

    public static bool IsPlausibleOverride(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length != OverrideSize)
        {
            return false;
        }

        for (var offset = 0; offset < OverrideSize; offset += sizeof(float))
        {
            var value = BitConverter.ToSingle(buffer[offset..(offset + sizeof(float))]);
            if (float.IsNaN(value) || value < 0 || (value > 100_000 && value != float.MaxValue))
            {
                return false;
            }
        }

        return true;
    }

    public static bool Matches(ReadOnlySpan<byte> current, ReadOnlySpan<byte> expected) =>
        current.SequenceEqual(expected);
}
