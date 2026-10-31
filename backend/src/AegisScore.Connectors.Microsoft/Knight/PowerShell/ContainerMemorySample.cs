using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AegisScore.Connectors.Microsoft.Knight.PowerShell;

/// <summary>Campos de <c>memory.stat</c> (cgroup v2) usados para separar o que cada parte ocupa no contêiner.</summary>
internal sealed record ContainerMemoryStat(
    long Anon, long File, long Shmem, long ActiveFile, long InactiveFile, long FileMapped, long FileDirty, long FileWriteback, long Kernel)
{
    /// <summary>Cache de arquivo limpo: <c>file</c> sem a memória compartilhada e o tmpfs (que sem swap não têm para onde ir) e sem páginas sujas.</summary>
    public long CleanFileCache => Math.Max(0, Math.Min(ActiveFile + InactiveFile, File - Shmem - FileDirty - FileWriteback));

    /// <summary><c>file_mapped</c> também conta a memória compartilhada mapeada (o código JIT do .NET fica nela): só o que excede <c>shmem</c> é arquivo.</summary>
    public long MappedFile => Math.Max(0, FileMapped - Shmem);

    private static readonly string[] RequiredFields = { "anon", "file", "shmem", "active_file", "inactive_file", "file_mapped", "file_dirty", "file_writeback" };

    internal static ContainerMemoryStat? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var space = line.IndexOf(' ');
            if (space > 0 && long.TryParse(line.AsSpan(space + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                values[line[..space]] = value;
        }
        // Sem um destes campos não dá para dizer o que é recuperável: vale a conta pelo total, nunca um desconto com o campo presumido em zero.
        foreach (var required in RequiredFields)
            if (!values.ContainsKey(required)) return null;
        long Of(string key) => values.GetValueOrDefault(key);
        return new(Of("anon"), Of("file"), Of("shmem"), Of("active_file"), Of("inactive_file"), Of("file_mapped"), Of("file_dirty"),
            Of("file_writeback"), Of("kernel"));
    }
}

/// <summary>Somente contadores do cgroup v2; ausência de limite não é interpretada como memória esgotada.</summary>
internal sealed record ContainerMemorySample(long CurrentBytes, long LimitBytes, ContainerMemoryStat? Stat = null, double? FullStallAvg10 = null)
{
    /// <summary>Tarefas paradas juntas esperando memória em ao menos esta fração dos últimos 10 s: o kernel já não recupera sem travar a API.</summary>
    public const double StallTriggerPercent = 40;

    /// <summary>
    /// [AEGIS-KNIGHT-MEMORY-BUDGET] O que o kernel devolve sem encerrar ninguém. Cache limpo não mapeado sempre conta. Arquivo limpo mapeado é
    /// código em uso: descartá-lo pode travar a API antes de faltar memória, e só o travamento medido revela isso. Por isso o mapeado acima do
    /// piso (1/8 do limite) só conta com PSI válido; sem ele, a guarda de travamento não existe e nada mapeado é descontado.
    /// </summary>
    public long ReclaimableBytes => Stat is { } stat
        ? Math.Max(0, stat.CleanFileCache - (FullStallAvg10 is null ? stat.MappedFile : Math.Min(stat.MappedFile, LimitBytes / 8)))
        : 0;

    /// <summary>Memória que não pode ser devolvida sem encerrar alguém. Sem o detalhe de <c>memory.stat</c>, vale o total.</summary>
    public long PressureBytes => CurrentBytes - ReclaimableBytes;

    /// <summary>O gatilho continua em 87,5% do limite do contêiner.</summary>
    public long TriggerBytes => LimitBytes - LimitBytes / 8;

    public bool UnderPressure => PressureBytes >= TriggerBytes || FullStallAvg10 >= StallTriggerPercent;

    /// <summary>Quanto ainda cabe até o gatilho.</summary>
    public long HeadroomBytes => TriggerBytes - PressureBytes;

    /// <summary>
    /// [AEGIS-KNIGHT-MEMORY-BUDGET] Folga mínima para iniciar um adaptador. Abaixo dela, o monitor encerraria o filho durante a importação,
    /// sem nenhuma leitura: é melhor dizer de saída. Na reprodução combinada do CI, a menor importação oficial somou cerca de 120 MiB à pressão.
    /// </summary>
    public long StartReserveBytes => LimitBytes / 4;

    public bool CanStartChild => HeadroomBytes >= StartReserveBytes;

    public static ContainerMemorySample? Read()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            return Parse(File.ReadAllText("/sys/fs/cgroup/memory.current"), File.ReadAllText("/sys/fs/cgroup/memory.max"),
                Optional("/sys/fs/cgroup/memory.stat"), Optional("/sys/fs/cgroup/memory.pressure"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? Optional(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static ContainerMemorySample? Parse(string current, string limit, string? stat = null, string? pressure = null) =>
        long.TryParse(current.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var used)
        && long.TryParse(limit.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var max)
        && used >= 0 && max > 0 ? new(used, max, ContainerMemoryStat.Parse(stat), ParseFullStall(pressure)) : null;

    /// <summary>Linha <c>full avg10=…</c> de <c>memory.pressure</c>. Nulo quando o PSI falta, não abre ou traz valor fora de 0 a 100: não encerra ninguém, mas retira o desconto de arquivo mapeado.</summary>
    internal static double? ParseFullStall(string? pressure)
    {
        if (string.IsNullOrWhiteSpace(pressure)) return null;
        foreach (var line in pressure.Split('\n'))
        {
            if (!line.StartsWith("full ", StringComparison.Ordinal)) continue;
            foreach (var part in line.Split(' '))
                if (part.StartsWith("avg10=", StringComparison.Ordinal)
                    && double.TryParse(part.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && value is >= 0 and <= 100) return value;
        }
        return null;
    }

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
        // [AEGIS-KNIGHT-MEMORY-BUDGET] O pwsh pede GC de servidor em tempo de execução; ele reserva por núcleo, sem relação com o contêiner.
        SetUnlessPresent(environment, "gcServer", "0");
        SetUnlessPresent(environment, "gcConcurrent", "0");
        SetUnlessPresent(environment, "TieredPGO", "0");
        // Processo curto: usa o código pré-compilado do pwsh sem recompilar em segundo plano e limita a geração 0 a 16 MiB (valor hexadecimal).
        SetUnlessPresent(environment, "TieredCompilation", "0");
        SetUnlessPresent(environment, "GCgen0MaxBudget", "1000000");
    }

    private static void SetUnlessPresent(IDictionary<string, string?> environment, string setting, string value)
    {
        if (!environment.ContainsKey("DOTNET_" + setting) && !environment.ContainsKey("COMPlus_" + setting))
            environment["DOTNET_" + setting] = value;
    }
}

/// <summary>Memória residente de UM processo (<c>/proc/&lt;pid&gt;/status</c>). Nulo fora do Linux ou se o processo já terminou.</summary>
internal sealed record ProcessMemorySample(long RssBytes, long AnonBytes, long FileBytes, long ShmemBytes)
{
    public static ProcessMemorySample? Read(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return Parse(File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/status")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static ProcessMemorySample? Parse(string status)
    {
        long rss = -1, anon = 0, file = 0, shmem = 0;
        foreach (var line in status.Split('\n'))
        {
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal)) rss = Kib(line);
            else if (line.StartsWith("RssAnon:", StringComparison.Ordinal)) anon = Kib(line);
            else if (line.StartsWith("RssFile:", StringComparison.Ordinal)) file = Kib(line);
            else if (line.StartsWith("RssShmem:", StringComparison.Ordinal)) shmem = Kib(line);
        }
        return rss < 0 ? null : new(rss, anon, file, shmem);
    }

    private static long Kib(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kib) ? kib * 1024 : 0;
    }
}

/// <summary>
/// O que o processo do adaptador e o contêiner ocuparam durante UMA execução: picos observados pelo monitor, memória da API no
/// início e heap do filho declarado pelo script. Só números, para o log do operador — heap gerenciado não é o limite total do processo.
/// </summary>
public sealed record PowerShellMemoryUsage(
    long LimitBytes,
    long StartContainerBytes,
    long StartReclaimableBytes,
    long ApiRssBytes,
    long ApiAnonBytes,
    long ApiManagedHeapBytes,
    long ApiCommittedBytes,
    long PeakContainerBytes,
    long PeakAnonBytes,
    long PeakShmemBytes,
    long PeakFileBytes,
    long PeakActiveFileBytes,
    long PeakInactiveFileBytes,
    long PeakPressureBytes,
    long ChildPeakRssBytes,
    long ChildPeakAnonBytes,
    long? ChildManagedHeapBytes,
    long? ChildCommittedBytes,
    double? PeakFullStallAvg10);
