using System.Globalization;
using System.Text.RegularExpressions;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// <see cref="ILLMClient"/> determinístico e sem rede, para DEV/demo e testes. Devolve um veredito
/// JSON bem-formado (o mesmo contrato do System Prompt do avaliador) a partir de uma varredura ingênua
/// de palavras-chave no payload — o suficiente para exercitar o pipeline evaluate→persist e os três
/// status, NÃO uma avaliação real. Para produção, registre um ILLMClient de verdade (Anthropic/OpenAI)
/// — o transporte HTTP do <see cref="ClaudeAssessmentService"/> serve de molde.
/// </summary>
public sealed class StubLlmClient : ILLMClient
{
    public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var p = userPrompt.ToLowerInvariant();
        var (status, evidence) = Evaluate(p);
        return Task.FromResult($"{{\"status\":\"{status}\",\"aiEvidence\":\"{evidence}\"}}");
    }

    /// <summary>
    /// Varredura ingênua de palavras-chave — determinística, sem rede. Distingue duas famílias de payload:
    /// (1) telemetria de ATIVO (Identify / ID.AM), reconhecida pelos rótulos "EDR Coverage:" / "OS Lifecycle:"
    /// compostos pelo <c>TelemetryIngestionService</c>; (2) telemetria genérica de EDR/SIEM. NÃO é avaliação
    /// real — só exercita o pipeline evaluate→persist e os três status.
    /// </summary>
    private static (string Status, string Evidence) Evaluate(string p)
    {
        // (1) Identify / Asset Management: sem EDR ou SO obsoleto REPROVA; EDR ativo + zero CVEs críticas APROVA;
        //     o meio-termo (EDR presente mas com vulnerabilidades/degradação) recebe crédito parcial.
        if (p.Contains("edr coverage:") || p.Contains("os lifecycle:"))
        {
            if (p.Contains("edr coverage: absent") || p.Contains("os lifecycle: endoflife"))
                return ("NonCompliant", "Stub: ativo exposto — EDR ausente ou sistema operacional em fim de vida (EOL).");
            if (p.Contains("edr coverage: active") && p.Contains("critical vulnerabilities: 0"))
                return ("Compliant", "Stub: EDR ativo e zero CVEs críticas — gestão do ativo em conformidade.");
            return ("MitigatedByThirdParty", "Stub: cobertura parcial do ativo — EDR presente, porém com vulnerabilidades ou degradação.");
        }

        // (2) Protect (PR) — regras de auditoria SÊNIOR por categoria (Tolerância Zero). Falha binária:
        //     qualquer condição de risco reprova; passando em todas, o controle é Compliant.
        if (EvaluateProtect(p) is { } protectVerdict)
            return protectVerdict;

        // (3) Detect (DE) — maturidade de SOC por categoria (Tolerância Zero, também binária).
        if (EvaluateDetect(p) is { } detectVerdict)
            return detectVerdict;

        // (4) Respond (RS) & Recover (RC) — resiliência a incidentes (Tolerância Zero, binária).
        if (EvaluateRespondRecover(p) is { } resilienceVerdict)
            return resilienceVerdict;

        // (5) Telemetria genérica de EDR/SIEM (Sentinel, CrowdStrike, Defender…).
        if (p.Contains("mssp") || p.Contains("managed service") || p.Contains("third party") || p.Contains("thirdparty"))
            return ("MitigatedByThirdParty", "Stub: log indica cobertura por serviço gerenciado/terceiro (SOC/MSSP).");
        if (p.Contains("blocked") || p.Contains("prevented") || p.Contains("\"mfa\":true") || p.Contains("success"))
            return ("Compliant", "Stub: telemetria mostra ação de bloqueio/MFA bem-sucedida para o controle alvo.");
        return ("NonCompliant", "Stub: sem evidência conclusiva de controle efetivo no payload analisado.");
    }

    /// <summary>
    /// Regras do pilar PROTECT, uma por categoria, reconhecidas pelos rótulos que o
    /// <c>TelemetryIngestionService</c> compõe. Faz parsing dos valores (não só keyword) para aplicar os
    /// limiares de auditoria. Retorna <c>null</c> quando o payload não é de Protect (segue o roteamento).
    /// </summary>
    private static (string Status, string Evidence)? EvaluateProtect(string p)
    {
        // PR.AA — Identity & Access: privilégio sem MFA integral OU sem Conditional Access = falha crítica.
        if (p.Contains("privileged mfa coverage:"))
        {
            var privMfa = Num(p, "privileged mfa coverage:");
            var conditionalAccess = Flag(p, "conditional access enforced:");
            return privMfa < 100 || !conditionalAccess
                ? ("NonCompliant", $"Stub: PR.AA reprovado — MFA privilegiado em {privMfa:0.#}% (exige 100%) ou Conditional Access desabilitado. Privilégio sem MFA é falha crítica.")
                : ("Compliant", "Stub: PR.AA conforme — MFA privilegiado integral e Conditional Access aplicado.");
        }

        // PR.DS — Data Security: cobertura de criptografia < 95% OU tráfego em claro detectado.
        if (p.Contains("endpoint encryption coverage:"))
        {
            var encryption = Num(p, "endpoint encryption coverage:");
            var unencrypted = Flag(p, "unencrypted traffic detected:");
            return encryption < 95 || unencrypted
                ? ("NonCompliant", $"Stub: PR.DS reprovado — criptografia de endpoint em {encryption:0.#}% (mínimo 95%) ou tráfego em claro detectado.")
                : ("Compliant", "Stub: PR.DS conforme — criptografia ampla e tráfego cifrado fim a fim.");
        }

        // PR.PS — Platform Security: hardening CIS < 80% OU qualquer patch crítico pendente.
        if (p.Contains("cis benchmark compliance rate:"))
        {
            var cis = Num(p, "cis benchmark compliance rate:");
            var missingPatches = Num(p, "missing critical patches:");
            return cis < 80 || missingPatches > 0
                ? ("NonCompliant", $"Stub: PR.PS reprovado — conformidade CIS em {cis:0.#}% (mínimo 80%) ou {missingPatches:0} patch(es) crítico(s) pendente(s).")
                : ("Compliant", "Stub: PR.PS conforme — benchmark CIS satisfatório e sem patches críticos pendentes.");
        }

        // PR.IR — Infrastructure Resilience: firewall sem política default-deny.
        if (p.Contains("default deny firewall enforced:") || p.Contains("microsegmentation active:"))
        {
            var defaultDeny = Flag(p, "default deny firewall enforced:");
            return !defaultDeny
                ? ("NonCompliant", "Stub: PR.IR reprovado — firewall sem política default-deny; perímetro permissivo.")
                : ("Compliant", "Stub: PR.IR conforme — firewall default-deny aplicado.");
        }

        return null;
    }

    /// <summary>
    /// Regras do pilar DETECT, uma por categoria (reconhecidas pelos rótulos que o
    /// <c>TelemetryIngestionService</c> compõe), com parsing dos valores. Retorna <c>null</c> quando o
    /// payload não é de Detect (segue o roteamento).
    /// </summary>
    private static (string Status, string Evidence)? EvaluateDetect(string p)
    {
        // DE.AE — Anomalies: qualquer anomalia grave NÃO investigada OU fadiga de alerta (FP > 50%).
        if (p.Contains("uninvestigated high anomalies:"))
        {
            var uninvestigated = Num(p, "uninvestigated high anomalies:");
            var falsePositive = Num(p, "false positive rate:");
            return uninvestigated > 0 || falsePositive > 50
                ? ("NonCompliant", $"Stub: DE.AE reprovado — {uninvestigated:0} anomalia(s) grave(s) não investigada(s) ou falso-positivo em {falsePositive:0.#}% (>50%). Fadiga/negligência de alerta é falha crítica.")
                : ("Compliant", "Stub: DE.AE conforme — anomalias graves investigadas e ruído de alerta sob controle.");
        }

        // DE.CM — Monitoring: cobertura de logs críticos < 95% OU qualquer ativo crítico não monitorado.
        if (p.Contains("critical log source coverage:"))
        {
            var logCoverage = Num(p, "critical log source coverage:");
            var unmonitored = Num(p, "unmonitored critical assets:");
            return logCoverage < 95 || unmonitored > 0
                ? ("NonCompliant", $"Stub: DE.CM reprovado — cobertura de logs críticos em {logCoverage:0.#}% (<95%) ou {unmonitored:0} ativo(s) crítico(s) sem monitoração. Ponto cego na coroa não é aceito.")
                : ("Compliant", "Stub: DE.CM conforme — logs críticos cobertos e sem ativos críticos fora do monitoramento.");
        }

        // Detection Engineering (DE.AE): cobertura MITRE ATT&CK < 40% OU ataques simulados detectados < 80%.
        if (p.Contains("mitre attck coverage rate:"))
        {
            var mitre = Num(p, "mitre attck coverage rate:");
            var simulated = Num(p, "simulated attacks detected rate:");
            return mitre < 40 || simulated < 80
                ? ("NonCompliant", $"Stub: detecção ineficaz — cobertura MITRE ATT&CK em {mitre:0.#}% (<40%) ou {simulated:0.#}% dos ataques simulados detectados (<80%). Regras não pegam ataques reais.")
                : ("Compliant", "Stub: engenharia de detecção eficaz — cobertura MITRE e taxa de detecção em exercícios satisfatórias.");
        }

        return null;
    }

    /// <summary>
    /// Regras dos pilares RESPOND (RS) e RECOVER (RC), reconhecidas pelos rótulos que o
    /// <c>TelemetryIngestionService</c> compõe, com parsing dos valores. Retorna <c>null</c> quando o
    /// payload não é de resiliência (segue o roteamento).
    /// </summary>
    private static (string Status, string Evidence)? EvaluateRespondRecover(string p)
    {
        // RS.MA — Incident Analysis: reconhecimento lento (MTTA > 30 min) OU threat hunting < 80%.
        if (p.Contains("mean time to acknowledge:"))
        {
            var mtta = Num(p, "mean time to acknowledge:");
            var hunting = Num(p, "threat hunting coverage rate:");
            return mtta > 30 || hunting < 80
                ? ("NonCompliant", $"Stub: RS.MA reprovado — MTTA de {mtta:0} min (>30) ou cobertura de threat hunting em {hunting:0.#}% (<80%). Resposta lenta ou caça a ameaças insuficiente.")
                : ("Compliant", "Stub: RS.MA conforme — reconhecimento ágil e cobertura de threat hunting satisfatória.");
        }

        // RS.MI — Incident Mitigation: sem isolamento automatizado OU contenção lenta (MTTR > 120 min).
        if (p.Contains("automated isolation enabled:") || p.Contains("mean time to respond:"))
        {
            var autoIsolation = Flag(p, "automated isolation enabled:");
            var mttr = Num(p, "mean time to respond:");
            return !autoIsolation || mttr > 120
                ? ("NonCompliant", $"Stub: RS.MI reprovado — sem isolamento automatizado ou MTTR de {mttr:0} min (>120). Contenção lenta amplia o dano.")
                : ("Compliant", "Stub: RS.MI conforme — isolamento automatizado ativo e contenção dentro do alvo.");
        }

        // RC.RP — Recovery Plan Execution: backup mutável OU integridade não-Valid OU RTO não atendido.
        if (p.Contains("immutable backups enabled:") || p.Contains("backup integrity status:"))
        {
            var immutable = Flag(p, "immutable backups enabled:");
            var integrityValid = p.Contains("backup integrity status: valid");
            var rtoMet = Flag(p, "recovery time objective met:");
            return !immutable || !integrityValid || !rtoMet
                ? ("NonCompliant", "Stub: RC.RP reprovado — backup sem imutabilidade, integridade não-Valid (corrompido/não testado) ou RTO não atendido. Recuperação não confiável contra ransomware.")
                : ("Compliant", "Stub: RC.RP conforme — backups imutáveis, íntegros (Valid) e RTO atendido.");
        }

        return null;
    }

    /// <summary>Extrai o número que segue um rótulo no payload (já lowercased). Fallback 0 se ausente.</summary>
    private static double Num(string p, string label)
    {
        var m = Regex.Match(p, Regex.Escape(label) + @"\s*(-?\d+(?:[.,]\d+)?)");
        return m.Success && double.TryParse(
            m.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v : 0;
    }

    /// <summary>Extrai o booleano (true/false) que segue um rótulo. Fallback false se ausente.</summary>
    private static bool Flag(string p, string label)
    {
        var m = Regex.Match(p, Regex.Escape(label) + @"\s*(true|false)");
        return m.Success && m.Groups[1].Value == "true";
    }
}
