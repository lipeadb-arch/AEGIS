using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AegisScore.Connectors.Microsoft.Knight.PowerShell;

/// <summary>Somente contadores do cgroup v2; ausência de limite não é interpretada como memória esgotada.</summary>
internal sealed record ContainerMemorySample(long CurrentBytes, long LimitBytes)
{
    public bool UnderPressure => CurrentBytes >= LimitBytes - LimitBytes / 8;

    public static ContainerMemorySample? Read()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            return Parse(File.ReadAllText("/sys/fs/cgroup/memory.current"),
                File.ReadAllText("/sys/fs/cgroup/memory.max"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static ContainerMemorySample? Parse(string current, string limit) =>
        long.TryParse(current.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var used)
        && long.TryParse(limit.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var max)
        && used >= 0 && max > 0 ? new(used, max) : null;

    internal static void ConfigureChildGc(IDictionary<string, string?> environment)
    {
        // API e pwsh dividem o contêiner. O padrão do .NET permite a CADA processo reservar 75% do limite.
        // Valores de heap no ambiente são hexadecimais: 19 = 25%. Configuração explícita do operador prevalece.
        var hasHeapSetting = false;
        foreach (var key in environment.Keys)
            if (key.StartsWith("DOTNET_GCHeapHardLimit", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("COMPlus_GCHeapHardLimit", StringComparison.OrdinalIgnoreCase)) hasHeapSetting = true;
        if (!hasHeapSetting) environment["DOTNET_GCHeapHardLimitPercent"] = "19";
        if (!environment.ContainsKey("DOTNET_GCConserveMemory") && !environment.ContainsKey("COMPlus_GCConserveMemory"))
            environment["DOTNET_GCConserveMemory"] = "6";
    }
}
