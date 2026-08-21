using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;

namespace AegisScore.Application.Assessment;

/// <summary>
/// Veredito determinístico de conformidade de UM controle a partir de telemetria — status, justificativa
/// factual e checklist técnico, todos decididos por REGRA em código.
/// </summary>
public sealed record DeterministicVerdict(
    ControlStatus Status,
    string Evidence,
    IReadOnlyList<ComplianceCheck> Checks,
    IReadOnlyList<MissingRequirement> MissingRequirements);

/// <summary>
/// [AEGIS-AUD-019] Autoridade determinística ÚNICA de avaliação de conformidade por telemetria. Concentra a
/// lógica que antes vivia (indevidamente) dentro do <c>StubLlmClient</c>: a varredura por rótulos e limiares
/// que classifica cada família de payload (Identify/Protect/Detect/Respond/Recover/Govern) em
/// Compliant/MitigatedByThirdParty/NonCompliant, decompõe o veredito no checklist técnico e compila as
/// lacunas de prova. É PURA (sem rede, sem EF, sem LLM), estática e testável — reutilizada tanto pelo
/// avaliador de telemetria (<c>AegisAiEvaluatorService</c>) quanto pelo próprio <c>StubLlmClient</c>, que
/// passou a apenas DELEGAR aqui. O LLM não decide status, pontos, subcategoria nem fórmula.
/// </summary>
public static class DeterministicControlEvaluator
{
    /// <summary>Marcador de simulação: o payload declara que a fonte de telemetria do controle NÃO está integrada.</summary>
    private const string TelemetryAbsentMarker = "telemetry source: absent";

    /// <summary>Marcador de simulação: o Document Hub JÁ processou uma política que cobre o controle.</summary>
    private const string DocumentProcessedMarker = "policy document: processed";

    /// <summary>
    /// Avalia UM controle contra o payload de telemetria, de forma determinística. A âncora de código
    /// (<paramref name="subcategoryCode"/>) é injetada no texto varrido para que regras multi-controle
    /// (ex.: o mesmo retrato de identidade do Entra ID → PR.AA-01 e GV.RR-01) discriminem pelo controle-alvo.
    /// <paramref name="evidenceType"/> é a natureza TIPADA da evidência (autoridade PERSISTIDA de
    /// classificação — telemetria × documental × híbrida); <paramref name="evidenceRequirements"/> (o
    /// <c>evidence_requirements</c> da regra) alimenta apenas a DESCRIÇÃO/identificador de fonte da lacuna.
    /// Ambos nulos quando não há regra extraída para o controle.
    /// </summary>
    public static DeterministicVerdict Evaluate(
        string subcategoryCode, string rawPayload,
        RuleEvidenceType? evidenceType, IReadOnlyList<string>? evidenceRequirements)
    {
        // Âncora de código + payload, minúsculo: o MESMO texto que o avaliador lia do User Prompt.
        var p = ($"subcategory: {subcategoryCode}\n{rawPayload}").ToLowerInvariant();

        var (statusText, evidence) = EvaluateRouting(p);
        var status = Enum.TryParse<ControlStatus>(statusText, ignoreCase: true, out var parsed)
            ? parsed
            : ControlStatus.NonCompliant;   // fail-closed: rótulo desconhecido nunca vira "conforme"

        return new DeterministicVerdict(
            status, evidence, BuildProtectChecks(p),
            BuildMissingRequirements(statusText, evidenceType, evidenceRequirements, p));
    }

    // ---- Roteamento por família de payload (Tolerância Zero, binária) -------------------------------

    private static (string Status, string Evidence) EvaluateRouting(string p)
    {
        // (1) Identify / Asset Management.
        if (p.Contains("edr coverage:") || p.Contains("os lifecycle:"))
        {
            if (p.Contains("edr coverage: absent") || p.Contains("os lifecycle: endoflife"))
                return ("NonCompliant", "Ativo exposto — EDR ausente ou sistema operacional em fim de vida (EOL).");
            if (p.Contains("edr coverage: active") && p.Contains("critical vulnerabilities: 0"))
                return ("Compliant", "EDR ativo e zero CVEs críticas — gestão do ativo em conformidade.");
            return ("MitigatedByThirdParty", "Cobertura parcial do ativo — EDR presente, porém com vulnerabilidades ou degradação.");
        }

        // (2) Protect (PR).
        if (EvaluateProtect(p) is { } protectVerdict) return protectVerdict;

        // (3) Detect (DE).
        if (EvaluateDetect(p) is { } detectVerdict) return detectVerdict;

        // (4) Respond (RS) & Recover (RC).
        if (EvaluateRespondRecover(p) is { } resilienceVerdict) return resilienceVerdict;

        // (4.5) Govern (GV) — vem ANTES do fallback genérico ("third party" casaria com "Third Party Audited:").
        if (EvaluateGovern(p) is { } governVerdict) return governVerdict;

        // (5) Telemetria genérica de EDR/SIEM.
        if (p.Contains("mssp") || p.Contains("managed service") || p.Contains("third party") || p.Contains("thirdparty"))
            return ("MitigatedByThirdParty", "Log indica cobertura por serviço gerenciado/terceiro (SOC/MSSP).");
        if (p.Contains("blocked") || p.Contains("prevented") || p.Contains("\"mfa\":true") || p.Contains("success"))
            return ("Compliant", "Telemetria mostra ação de bloqueio/MFA bem-sucedida para o controle alvo.");
        return ("NonCompliant", "Sem evidência conclusiva de controle efetivo no payload analisado.");
    }

    private static (string Status, string Evidence)? EvaluateProtect(string p)
    {
        // PR.AA — Identity & Access: privilégio sem MFA integral OU sem Conditional Access = falha crítica.
        if (p.Contains("privileged mfa coverage:"))
        {
            var privMfa = Num(p, "privileged mfa coverage:");
            var conditionalAccess = Flag(p, "conditional access enforced:");
            return privMfa < 100 || !conditionalAccess
                ? ("NonCompliant", $"PR.AA reprovado — MFA privilegiado em {privMfa:0.#}% (exige 100%) ou Conditional Access desabilitado. Privilégio sem MFA é falha crítica.")
                : ("Compliant", "PR.AA conforme — MFA privilegiado integral e Conditional Access aplicado.");
        }

        // PR.AA — Identity Posture (Entra ID), ancorado no controle; pondera controle compensatório OT/IoT.
        if (TargetsControl(p, "pr.aa") && p.Contains("privileged accounts without mfa:"))
        {
            var privWithoutMfa = Num(p, "privileged accounts without mfa:");
            var totalPriv = Num(p, "total privileged accounts:");
            if (privWithoutMfa <= 0)
                return ("Compliant", $"PR.AA conforme — todas as {totalPriv:0} contas privilegiadas do Entra ID com MFA efetivo.");

            var exemptServiceAccounts = Num(p, "mfa-exempt service accounts:");
            var networkIsolation = p.Contains("network isolation = true");
            if (exemptServiceAccounts > 0 && networkIsolation)
                return ("MitigatedByThirdParty", $"PR.AA mitigado — {privWithoutMfa:0} conta(s) sem MFA correspondem a serviço/OT ({exemptServiceAccounts:0} isenta(s) por legado) e o ativo está ISOLADO na rede (controle compensatório). Falso positivo de ambiente industrial evitado.");

            return ("NonCompliant", $"PR.AA reprovado — {privWithoutMfa:0} de {totalPriv:0} conta(s) privilegiada(s) do Entra ID sem MFA e SEM controle compensatório (isolamento de rede). Privilégio sem MFA é falha crítica (PoLP).");
        }

        // PR.DS — Data Security.
        if (p.Contains("endpoint encryption coverage:"))
        {
            var encryption = Num(p, "endpoint encryption coverage:");
            var unencrypted = Flag(p, "unencrypted traffic detected:");
            return encryption < 95 || unencrypted
                ? ("NonCompliant", $"PR.DS reprovado — criptografia de endpoint em {encryption:0.#}% (mínimo 95%) ou tráfego em claro detectado.")
                : ("Compliant", "PR.DS conforme — criptografia ampla e tráfego cifrado fim a fim.");
        }

        // PR.PS — Platform Security.
        if (p.Contains("cis benchmark compliance rate:"))
        {
            var cis = Num(p, "cis benchmark compliance rate:");
            var missingPatches = Num(p, "missing critical patches:");
            return cis < 80 || missingPatches > 0
                ? ("NonCompliant", $"PR.PS reprovado — conformidade CIS em {cis:0.#}% (mínimo 80%) ou {missingPatches:0} patch(es) crítico(s) pendente(s).")
                : ("Compliant", "PR.PS conforme — benchmark CIS satisfatório e sem patches críticos pendentes.");
        }

        // PR.IR — Infrastructure Resilience.
        if (p.Contains("default deny firewall enforced:") || p.Contains("microsegmentation active:"))
        {
            var defaultDeny = Flag(p, "default deny firewall enforced:");
            return !defaultDeny
                ? ("NonCompliant", "PR.IR reprovado — firewall sem política default-deny; perímetro permissivo.")
                : ("Compliant", "PR.IR conforme — firewall default-deny aplicado.");
        }

        return null;
    }

    /// <summary>Decompõe as métricas do PROTECT no checklist técnico que justifica o veredito (vazio fora do Protect).</summary>
    private static IReadOnlyList<ComplianceCheck> BuildProtectChecks(string p)
    {
        var checks = new List<ComplianceCheck>();

        if (p.Contains("privileged mfa coverage:"))
        {
            var privMfa = Num(p, "privileged mfa coverage:");
            checks.Add(new("MFA Privilegiado Integral", privMfa >= 100, $"MFA em contas privilegiadas: {privMfa:0.#}% (exige 100%)."));
            checks.Add(new("Conditional Access Aplicado", Flag(p, "conditional access enforced:"), "Políticas de Conditional Access ativas no acesso."));
        }

        if (p.Contains("privileged accounts without mfa:"))
        {
            var without = Num(p, "privileged accounts without mfa:");
            var total = Num(p, "total privileged accounts:");
            checks.Add(new("Contas Privilegiadas com MFA", without <= 0, $"{without:0} de {total:0} contas privilegiadas sem MFA."));
            checks.Add(new("Isolamento de Rede (OT)", p.Contains("network isolation = true"), "Ativos sem MFA em rede isolada (controle compensatório)."));
        }

        if (p.Contains("endpoint encryption coverage:"))
        {
            var enc = Num(p, "endpoint encryption coverage:");
            checks.Add(new("Endpoint Encrypted", enc >= 95, $"Criptografia de endpoint em {enc:0.#}% (mínimo 95%)."));
            checks.Add(new("No Unencrypted Traffic", !Flag(p, "unencrypted traffic detected:"), "Ausência de tráfego em claro na rede."));
        }

        if (p.Contains("cis benchmark compliance rate:"))
        {
            var cis = Num(p, "cis benchmark compliance rate:");
            var patches = Num(p, "missing critical patches:");
            checks.Add(new("CIS Hardening", cis >= 80, $"Conformidade CIS em {cis:0.#}% (mínimo 80%)."));
            checks.Add(new("No Critical Patches Pending", patches <= 0, $"{patches:0} patch(es) crítico(s) pendente(s)."));
        }

        if (p.Contains("default deny firewall enforced:"))
            checks.Add(new("Default-Deny Firewall", Flag(p, "default deny firewall enforced:"), "Firewall com política default-deny (perímetro restritivo)."));

        return checks;
    }

    private static (string Status, string Evidence)? EvaluateDetect(string p)
    {
        // DE.AE — Anomalies.
        if (p.Contains("uninvestigated high anomalies:"))
        {
            var uninvestigated = Num(p, "uninvestigated high anomalies:");
            var falsePositive = Num(p, "false positive rate:");
            return uninvestigated > 0 || falsePositive > 50
                ? ("NonCompliant", $"DE.AE reprovado — {uninvestigated:0} anomalia(s) grave(s) não investigada(s) ou falso-positivo em {falsePositive:0.#}% (>50%). Fadiga/negligência de alerta é falha crítica.")
                : ("Compliant", "DE.AE conforme — anomalias graves investigadas e ruído de alerta sob controle.");
        }

        // DE.CM — Monitoring.
        if (p.Contains("critical log source coverage:"))
        {
            var logCoverage = Num(p, "critical log source coverage:");
            var unmonitored = Num(p, "unmonitored critical assets:");
            return logCoverage < 95 || unmonitored > 0
                ? ("NonCompliant", $"DE.CM reprovado — cobertura de logs críticos em {logCoverage:0.#}% (<95%) ou {unmonitored:0} ativo(s) crítico(s) sem monitoração. Ponto cego na coroa não é aceito.")
                : ("Compliant", "DE.CM conforme — logs críticos cobertos e sem ativos críticos fora do monitoramento.");
        }

        // Detection Engineering (DE.AE).
        if (p.Contains("mitre attck coverage rate:"))
        {
            var mitre = Num(p, "mitre attck coverage rate:");
            var simulated = Num(p, "simulated attacks detected rate:");
            return mitre < 40 || simulated < 80
                ? ("NonCompliant", $"Detecção ineficaz — cobertura MITRE ATT&CK em {mitre:0.#}% (<40%) ou {simulated:0.#}% dos ataques simulados detectados (<80%). Regras não pegam ataques reais.")
                : ("Compliant", "Engenharia de detecção eficaz — cobertura MITRE e taxa de detecção em exercícios satisfatórias.");
        }

        return null;
    }

    private static (string Status, string Evidence)? EvaluateRespondRecover(string p)
    {
        // RS.MA — Incident Analysis.
        if (p.Contains("mean time to acknowledge:"))
        {
            var mtta = Num(p, "mean time to acknowledge:");
            var hunting = Num(p, "threat hunting coverage rate:");
            return mtta > 30 || hunting < 80
                ? ("NonCompliant", $"RS.MA reprovado — MTTA de {mtta:0} min (>30) ou cobertura de threat hunting em {hunting:0.#}% (<80%). Resposta lenta ou caça a ameaças insuficiente.")
                : ("Compliant", "RS.MA conforme — reconhecimento ágil e cobertura de threat hunting satisfatória.");
        }

        // RS.MI — Incident Mitigation.
        if (p.Contains("automated isolation enabled:") || p.Contains("mean time to respond:"))
        {
            var autoIsolation = Flag(p, "automated isolation enabled:");
            var mttr = Num(p, "mean time to respond:");
            return !autoIsolation || mttr > 120
                ? ("NonCompliant", $"RS.MI reprovado — sem isolamento automatizado ou MTTR de {mttr:0} min (>120). Contenção lenta amplia o dano.")
                : ("Compliant", "RS.MI conforme — isolamento automatizado ativo e contenção dentro do alvo.");
        }

        // RC.RP — Recovery Plan Execution.
        if (p.Contains("immutable backups enabled:") || p.Contains("backup integrity status:"))
        {
            var immutable = Flag(p, "immutable backups enabled:");
            var integrityValid = p.Contains("backup integrity status: valid");
            var rtoMet = Flag(p, "recovery time objective met:");
            return !immutable || !integrityValid || !rtoMet
                ? ("NonCompliant", "RC.RP reprovado — backup sem imutabilidade, integridade não-Valid (corrompido/não testado) ou RTO não atendido. Recuperação não confiável contra ransomware.")
                : ("Compliant", "RC.RP conforme — backups imutáveis, íntegros (Valid) e RTO atendido.");
        }

        return null;
    }

    private static (string Status, string Evidence)? EvaluateGovern(string p)
    {
        // GV.SC — Supply Chain Risk Mgmt.
        if (p.Contains("suppliers with network access:") || p.Contains("third party audited:"))
        {
            var netAccessSuppliers = Num(p, "suppliers with network access:");
            var thirdPartyAudited = Flag(p, "third party audited:");
            return netAccessSuppliers > 0 && !thirdPartyAudited
                ? ("NonCompliant", $"GV.SC reprovado — {netAccessSuppliers:0} fornecedor(es) de TI com acesso à rede sem auditoria de terceiros ativa. Elo da cadeia de suprimentos não verificado.")
                : ("Compliant", "GV.SC conforme — fornecedores com acesso à rede sob auditoria de terceiros ativa (ou sem exposição de rede a terceiros).");
        }

        // GV.RR — Roles & Responsibilities.
        if (p.Contains("admin accounts without periodic review:") || p.Contains("privileged access review configured:"))
        {
            var adminsWithoutReview = Num(p, "admin accounts without periodic review:");
            var reviewConfigured = Flag(p, "privileged access review configured:");
            return adminsWithoutReview > 0 || !reviewConfigured
                ? ("NonCompliant", $"GV.RR reprovado — {adminsWithoutReview:0} conta(s) de administrador sem revisão periódica, ou revisão de acesso privilegiado não configurada. Autoridade sem accountability.")
                : ("Compliant", "GV.RR conforme — contas de administrador sob revisão periódica de acesso configurada.");
        }

        // GV.RR — Identity Governance (Entra ID), ancorado no controle (excesso de admins > 10).
        if (TargetsControl(p, "gv.rr") && p.Contains("total privileged accounts:"))
        {
            var totalPriv = Num(p, "total privileged accounts:");
            return totalPriv > 10
                ? ("NonCompliant", $"GV.RR reprovado — {totalPriv:0} contas privilegiadas (>10) no Entra ID. Excesso de administradores quebra o menor privilégio e a governança de identidade.")
                : ("Compliant", $"GV.RR conforme — {totalPriv:0} contas privilegiadas (≤10), aderente ao menor privilégio.");
        }

        return null;
    }

    /// <summary>
    /// Compila as LACUNAS DE EVIDÊNCIA (telemetria × documento) de um veredito não-conforme. Delega ao
    /// <see cref="RuleEvaluator"/> — o mesmo motor puro do resto do produto — usando o
    /// <c>evidence_requirements</c> da regra e os marcadores de simulação presentes no payload.
    /// </summary>
    private static IReadOnlyList<MissingRequirement> BuildMissingRequirements(
        string status, RuleEvidenceType? evidenceType, IReadOnlyList<string>? evidenceRequirements, string p)
    {
        if (status != "NonCompliant")
            return Array.Empty<MissingRequirement>();
        if (evidenceType is null || evidenceRequirements is null || evidenceRequirements.Count == 0)
            return Array.Empty<MissingRequirement>();   // sem regra não há como afirmar a natureza da prova

        // Classificação da lacuna pelo tipo PERSISTIDO (autoridade única), não por re-inferência da string.
        return RuleEvaluator.Compile(
            evidenceType.Value,
            evidenceRequirements,
            hasTelemetrySignal: !p.Contains(TelemetryAbsentMarker),
            hasProcessedDocument: p.Contains(DocumentProcessedMarker));
    }

    /// <summary>True se o payload MIRA o controle indicado (prefixo NIST, ex.: "pr.aa"/"gv.rr").</summary>
    private static bool TargetsControl(string p, string codePrefix) =>
        p.Contains("subcategory: " + codePrefix) || p.Contains("(control " + codePrefix);

    /// <summary>Extrai o número que segue um rótulo no payload (já minúsculo). Fallback 0 se ausente.</summary>
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
