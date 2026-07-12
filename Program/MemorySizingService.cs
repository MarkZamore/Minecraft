namespace Minecraft;

public static class MemorySizingService
{
    public const int MinMemoryGb = 2;
    public const int MaxMemoryGb = 128;
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    public static int GetAllowedMaxMemoryGb()
    {
        try
        {
            var bytes = new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory;
            return GetAllowedMaxMemoryGb(bytes);
        }
        catch
        {
            return MaxMemoryGb;
        }
    }

    public static int GetRecommendedDefaultMemoryGb()
    {
        try
        {
            var bytes = new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory;
            return GetRecommendedDefaultMemoryGb(bytes);
        }
        catch
        {
            return 16;
        }
    }

    public static int GetRecommendedDefaultMemoryGb(ulong totalPhysicalMemoryBytes)
    {
        var installedGb = (int)Math.Round(totalPhysicalMemoryBytes / BytesPerGb, MidpointRounding.AwayFromZero);
        var recommended = installedGb switch
        {
            < 12 => 6,
            < 24 => 8,
            _ => 16
        };
        return Math.Clamp(recommended, MinMemoryGb, GetAllowedMaxMemoryGb(totalPhysicalMemoryBytes));
    }

    public static int ClampMemoryGb(int value)
    {
        return Math.Clamp(value, MinMemoryGb, GetAllowedMaxMemoryGb());
    }

    public static int GetAllowedMaxMemoryGb(ulong totalPhysicalMemoryBytes)
    {
        var availableGb = (int)Math.Floor(totalPhysicalMemoryBytes / BytesPerGb);
        return Math.Clamp(availableGb, MinMemoryGb, MaxMemoryGb);
    }
}
