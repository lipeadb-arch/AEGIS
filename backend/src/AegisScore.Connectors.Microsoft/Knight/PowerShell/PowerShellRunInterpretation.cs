using System;
using System.Globalization;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Connectors.Microsoft.Knight.PowerShell;

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Interpretação ÚNICA de um processo de adaptador que não terminou íntegro, usada pelos
/// coletores do Teams, do Exchange Online e de proteção. As três regras:
///   • "nenhuma leitura foi tentada" só quando a saída COMPLETA mostra que nenhuma leitura começou;
///   • leitura iniciada e não concluída é falha DESSA leitura, com a terminação do processo como motivo;
///   • leitura não alcançada é "não tentada", com o estágio em que o processo parou — ou "não se sabe", quando a saída foi
///     cortada e não dá para afirmar.
/// Código diferente de zero, resultado ausente, tempo esgotado e saída cortada nunca produzem coleta "concluída".
/// </summary>
public static class PowerShellRunInterpretation
{
    /// <summary>Desfecho de uma capacidade sem registro de leitura num processo sem integridade.</summary>
    public sealed record MissingRead(KnightCapabilityOutcome Outcome, string Detail, KnightCapabilityDiagnostic Diagnostic);

    /// <summary>O processo terminou sem integridade ANTES de qualquer leitura começar (e sem conexão recusada declarada)?</summary>
    public static bool FailedBeforeReads(PowerShellRunReport? report) =>
        report is { Clean: false } && report.StartedReads.Count == 0 && !report.Reached("connect-failed");

    /// <summary>Desfecho de TODAS as capacidades quando o processo falhou antes das leituras.</summary>
    public static MissingRead BeforeReads(string label, PowerShellRunReport report, string? correlationId)
    {
        var how = How(report);
        var detail = report.OutputComplete
            ? $"O adaptador de coleta de {label} terminou antes de qualquer leitura: {how}, no estágio «{StageLabel(report.LastStage)}». "
              + "Nenhuma leitura foi tentada — e nenhum controle foi avaliado a partir de coleta vazia."
            : $"O adaptador de coleta de {label} foi encerrado e não é possível determinar se alguma leitura foi tentada: {how}. "
              + "Nenhum controle foi avaliado a partir de coleta vazia.";
        return new MissingRead(OutcomeOf(report), Trim(detail), Diagnostic(report, correlationId));
    }

    /// <summary>
    /// O script declarou estágios, mas nunca chegou a tentar a conexão (falhou na leitura do pedido ou na importação do módulo).
    /// Nesse caso a categoria que ele devolve NÃO é de conexão — e "a conexão não foi estabelecida" seria o motivo errado.
    /// </summary>
    public static bool FailedBeforeConnect(PowerShellRunReport? report) =>
        report is { Stages.Count: > 0 } && !report.Reached("connect") && report.StartedReads.Count == 0;

    /// <summary>Desfecho de todas as capacidades quando o script falhou antes de tentar a conexão.</summary>
    public static MissingRead BeforeConnect(string label, PowerShellRunReport report, string? errorId, string? correlationId)
    {
        var stage = report.LastStage;
        var kind = stage is "import" or "imported" ? KnightFailureKind.RuntimeUnavailable : KnightFailureKind.Internal;
        var detail = $"O adaptador de coleta de {label} falhou antes de conectar, no estágio «{StageLabel(stage)}»"
            + (kind == KnightFailureKind.RuntimeUnavailable ? " — o módulo oficial não carregou neste ambiente" : "")
            + ". Nenhuma leitura foi tentada — e nenhum controle foi avaliado a partir de coleta vazia.";
        return new MissingRead(KnightCapabilityOutcome.Error, Trim(detail),
            new KnightCapabilityDiagnostic(stage ?? "process-start", kind, report.DurationMs, Code: errorId, CorrelationId: correlationId));
    }

    /// <summary>Desfecho de UMA capacidade sem registro de leitura, pelo que o processo declarou antes de terminar.</summary>
    public static MissingRead Missing(KnightCapability capability, string label, PowerShellRunReport report, string? correlationId)
    {
        var name = capability.ToString();
        var how = How(report);
        if (report.StartedReads.Contains(name))
            return new MissingRead(OutcomeOf(report),
                Trim($"{label}: a leitura foi iniciada e o processo do adaptador terminou antes de concluí-la — {how}."),
                Diagnostic(report, correlationId) with { Stage = "read " + name });

        if (!report.OutputComplete)
            return new MissingRead(OutcomeOf(report),
                Trim($"{label}: não é possível determinar se esta leitura foi tentada — {how}."),
                Diagnostic(report, correlationId));

        return new MissingRead(KnightCapabilityOutcome.NotAttempted,
            Trim($"{label}: leitura não tentada — o processo do adaptador terminou antes dela, no estágio «{StageLabel(report.LastStage)}» ({how})."),
            Diagnostic(report, correlationId));
    }

    /// <summary>
    /// O resultado de um processo sem integridade nunca é "coleta concluída": o estado cai para parcial e o detalhe diz como o
    /// processo terminou. As leituras concluídas antes do fim são preservadas (cada uma terminou e emitiu o próprio registro).
    /// </summary>
    public static KnightCollectionResult Qualify(KnightCollectionResult result, PowerShellRunReport? report, string label)
    {
        if (report is null || report.Clean) return result;
        var state = result.State == KnightSourceState.Completed ? KnightSourceState.PartialCollection : result.State;
        var note = $"O processo do adaptador de {label} {How(report)} (último estágio: «{StageLabel(report.LastStage)}»); as leituras "
            + "concluídas antes disso foram preservadas e a coleta não é declarada íntegra.";
        return result with { State = state, Detail = string.IsNullOrWhiteSpace(result.Detail) ? note : result.Detail + " " + note };
    }

    /// <summary>Desfecho de uma falha de PROCESSO: tempo esgotado é indisponibilidade; o resto é falha da coleta (aplicação/ambiente).</summary>
    public static KnightCapabilityOutcome OutcomeOf(PowerShellRunReport report) =>
        report.Termination == PowerShellTermination.TimedOut ? KnightCapabilityOutcome.Unavailable : KnightCapabilityOutcome.Error;

    public static KnightFailureKind KindOf(PowerShellRunReport report) => report.Termination switch
    {
        PowerShellTermination.TimedOut => KnightFailureKind.Timeout,
        PowerShellTermination.OutputLimit => KnightFailureKind.OutputLimit,
        PowerShellTermination.MemoryLimit => KnightFailureKind.Internal,
        PowerShellTermination.NonZeroExit => KnightFailureKind.ProcessExit,
        PowerShellTermination.NoResult => KnightFailureKind.ProcessNoResult,
        PowerShellTermination.OutputIncomplete => KnightFailureKind.Incomplete,
        PowerShellTermination.Completed when !report.OutputComplete || !report.ResultEmitted => KnightFailureKind.Incomplete,
        _ => KnightFailureKind.Contract,
    };

    public static KnightCapabilityDiagnostic Diagnostic(PowerShellRunReport report, string? correlationId) =>
        new(report.LastStage ?? "process-start", KindOf(report), report.DurationMs,
            Code: report.ExitCode is { } code ? "exit:" + code.ToString(CultureInfo.InvariantCulture) : report.Termination.ToString(),
            CorrelationId: correlationId);

    /// <summary>Como o processo terminou, em palavras — com o que o código de saída permite afirmar, e só isso.</summary>
    public static string How(PowerShellRunReport report)
    {
        var text = report.Termination switch
        {
            PowerShellTermination.TimedOut =>
                $"não terminou no tempo limite e foi encerrado pelo AEGIS após {report.DurationMs / 1000} s",
            PowerShellTermination.OutputLimit => "a saída ultrapassou o limite aceito e o processo foi encerrado pelo AEGIS",
            PowerShellTermination.MemoryLimit => "foi encerrado pelo AEGIS por pressão de memória no contêiner, para preservar a aplicação; "
                + "isso não indica falta de permissão e somente as leituras concluídas foram preservadas",
            PowerShellTermination.NoResult => "terminou com código 0 sem emitir o documento de resultado",
            PowerShellTermination.OutputIncomplete =>
                "terminou com código 0, mas a saída dele não foi recebida até o fim (a leitura falhou ou o cano não fechou no prazo); "
                + "só vale o que chegou",
            PowerShellTermination.Completed when !report.OutputComplete || !report.ResultEmitted =>
                "não teve a saída recebida por inteiro",
            PowerShellTermination.NonZeroExit => ExitText(report.ExitCode),
            _ => "emitiu registro(s) fora do protocolo do adaptador",
        };
        return report.MalformedFrames > 0 && report.Termination != PowerShellTermination.Completed
            ? text + $"; {report.MalformedFrames} registro(s) ilegível(is)"
            : text;
    }

    private static string ExitText(int? code) => code switch
    {
        null => "terminou sem código de saída conhecido",
        137 when !OperatingSystem.IsWindows() =>
            "terminou com código 137 (encerrado pelo sinal 9 — compatível com falta de memória no contêiner ou encerramento externo; "
            + "o código não distingue entre os dois)",
        143 when !OperatingSystem.IsWindows() => "terminou com código 143 (encerrado pelo sinal 15 — encerramento solicitado externamente)",
        > 128 and < 160 when !OperatingSystem.IsWindows() =>
            $"terminou com código {code} (encerrado pelo sinal {code - 128})",
        _ => $"terminou com código {code}",
    };

    /// <summary>Estágios do protocolo em palavras.</summary>
    public static string StageLabel(string? stage) => stage switch
    {
        null => "antes de o script declarar qualquer estágio (o runtime não chegou a executá-lo)",
        "start" => "início do script",
        "input" => "leitura do pedido",
        "import" => "importação do módulo oficial",
        "imported" => "módulo importado",
        "connect" => "conexão com o serviço",
        "connected" => "sessão estabelecida",
        "connect-failed" => "conexão recusada",
        "compliance-connect" => "conexão com o Security & Compliance",
        "disconnect" => "encerramento da sessão",
        "result" => "emissão do resultado",
        _ when stage.StartsWith("read ", StringComparison.Ordinal) => "leitura " + stage[5..],
        _ => stage,
    };

    /// <summary>
    /// Linha de verificação do processo para os gates de runtime da imagem: como terminou, estágios, linhas fora do protocolo e
    /// duração. Devolve <c>false</c> (FALHA) quando o processo não terminou íntegro.
    /// </summary>
    public static bool CheckProcess(System.IO.TextWriter output, PowerShellRunReport? report, string scenario)
    {
        if (report is null)
        {
            // Leitor sem processo (substituto de teste): não há o que verificar aqui. O leitor real sempre devolve o relatório.
            output.WriteLine($"processo ({scenario}): relatório não disponível neste leitor.");
            return true;
        }
        output.WriteLine(
            $"processo ({scenario}): terminação={report.Termination} código={report.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"} "
            + $"estágios={string.Join(">", report.Stages)} linhas-fora-do-protocolo={report.NoiseLines} "
            + $"quadros-ilegíveis={report.MalformedFrames} duraçãoMs={report.DurationMs}");
        if (report.Clean) return true;
        output.WriteLine($"FALHA ({scenario}): o processo do adaptador {How(report)}.");
        return false;
    }

    private static string Trim(string text) => text.Length <= 480 ? text : text[..479] + "…";
}
