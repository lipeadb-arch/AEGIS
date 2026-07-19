using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Assessment;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;

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
    /// <summary>
    /// Marcador de simulação: o payload declara que a fonte de telemetria do controle NÃO está integrada
    /// (Cenário A — Blind Spot). Sem ele, o Stub assume que o sinal EXISTE — e assume certo: se há um
    /// payload de telemetria sendo avaliado, a fonte respondeu; a reprovação é de mérito, não de prova.
    /// </summary>
    private const string TelemetryAbsentMarker = "telemetry source: absent";

    /// <summary>
    /// Marcador de simulação: o Document Hub JÁ processou uma política que cobre o controle (Cenário B
    /// invertido). Sem ele, o Stub assume que NÃO há documento — o Stub não consulta o Document Hub, e
    /// assumir cobertura documental inexistente esconderia uma lacuna real de governança.
    /// </summary>
    private const string DocumentProcessedMarker = "policy document: processed";

    public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var p = userPrompt.ToLowerInvariant();
        var (status, evidence) = Evaluate(p);
        // Além do status, decompõe o veredito do Protect no CHECKLIST técnico (vazio nas demais famílias).
        // JsonSerializer garante o escaping de aspas/acentos na evidência e nos checks (era interpolação crua).
        var json = JsonSerializer.Serialize(new
        {
            status,
            aiEvidence = evidence,
            checks = BuildProtectChecks(p),
            missingRequirements = BuildMissingRequirements(status, userPrompt, p),
        });
        return Task.FromResult(json);
    }

    /// <summary>
    /// Compila as LACUNAS DE EVIDÊNCIA do veredito — a distinção "falta o log" × "falta a política" que a
    /// UI traduz em ícone de rede × ícone de pasta.
    ///
    /// Só emite em NÃO-CONFORMIDADE: controle conforme não tem pendência (o <c>ControlStateWriter</c>
    /// impõe a mesma invariante no ledger, então isto aqui é coerência, não a única guarda).
    ///
    /// NÃO reimplementa a classificação: delega ao <see cref="RuleEvaluator"/>, o mesmo motor puro que o
    /// avaliador real usará. A fonte das exigências é o bloco "EXPECTED EVIDENCE SOURCES" que o
    /// <c>AssessmentRuleContextBuilder</c> injeta no User Prompt a partir da regra do 800-53 — o Stub lê a
    /// regra REAL do catálogo, sem tocar o banco e sem uma tabela de exigências paralela.
    /// </summary>
    private static IReadOnlyList<MissingRequirement> BuildMissingRequirements(
        string status, string userPrompt, string lowered)
    {
        if (status != "NonCompliant")
            return Array.Empty<MissingRequirement>();

        var evidenceSources = ExtractEvidenceSources(userPrompt);
        if (evidenceSources.Count == 0)
            return Array.Empty<MissingRequirement>();   // sem regra no prompt não há como afirmar a natureza

        return RuleEvaluator.Compile(
            evidenceSources,
            hasTelemetrySignal: !lowered.Contains(TelemetryAbsentMarker),
            hasProcessedDocument: lowered.Contains(DocumentProcessedMarker));
    }

    /// <summary>
    /// Isola as linhas "  • …" do bloco EXPECTED EVIDENCE SOURCES do User Prompt. Lê o texto ORIGINAL
    /// (não o lowercased) porque o identificador da fonte vai para a tela: "Entra ID", não "entra id".
    /// O bloco EVALUATION METRICS usa o mesmo marcador de item, daí a leitura ancorada no cabeçalho.
    /// </summary>
    private static IReadOnlyList<string> ExtractEvidenceSources(string userPrompt)
    {
        const string header = "EXPECTED EVIDENCE SOURCES";
        var start = userPrompt.IndexOf(header, StringComparison.Ordinal);
        if (start < 0)
            return Array.Empty<string>();

        var sources = new List<string>();
        foreach (var line in userPrompt[start..].Split('\n').Skip(1))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("•", StringComparison.Ordinal))
                sources.Add(trimmed[1..].Trim());
            else if (sources.Count > 0 && trimmed.Length == 0)
                break;    // linha em branco encerra o bloco — o próximo é a telemetria crua
        }

        return sources;
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

        // (4.5) Govern (GV) — governança estruturada além dos documentos: cadeia de suprimentos (GV.SC) e
        //       papéis/autoridades (GV.RR). VEM ANTES do fallback genérico (5): o "third party" de lá casaria
        //       com o rótulo "Third Party Audited:" e mascararia o veredito real de GV.SC.
        if (EvaluateGovern(p) is { } governVerdict)
            return governVerdict;

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

        // PR.AA — Identity Posture (telemetria do Entra ID). Indicador "MFA not configured for privileged
        // accounts". Ancorado no controle-alvo porque o MESMO retrato também alimenta GV.RR-01 — a âncora
        // garante que esta regra decida só PR.AA. NÃO reprova cegamente: pondera CONTROLE COMPENSATÓRIO para
        // ambientes industriais (OT/IoT), onde terminais fabris não suportam MFA por legado mas vivem em rede
        // isolada — o falso positivo clássico das ferramentas de assessment de mercado.
        if (TargetsControl(p, "pr.aa") && p.Contains("privileged accounts without mfa:"))
        {
            var privWithoutMfa = Num(p, "privileged accounts without mfa:");
            var totalPriv = Num(p, "total privileged accounts:");
            if (privWithoutMfa <= 0)
                return ("Compliant", $"Stub: PR.AA conforme — todas as {totalPriv:0} contas privilegiadas do Entra ID com MFA efetivo.");

            // Há privilégio sem MFA. Controle compensatório: contas de serviço/OT isentas (limitação técnica)
            // E ativo isolado na rede → risco ATENUADO (mitigado), não falha crítica cega.
            var exemptServiceAccounts = Num(p, "mfa-exempt service accounts:");
            var networkIsolation = p.Contains("network isolation = true");
            if (exemptServiceAccounts > 0 && networkIsolation)
                return ("MitigatedByThirdParty", $"Stub: PR.AA mitigado — {privWithoutMfa:0} conta(s) sem MFA correspondem a serviço/OT ({exemptServiceAccounts:0} isenta(s) por legado) e o ativo está ISOLADO na rede (controle compensatório). Falso positivo de ambiente industrial evitado.");

            // (Evolução no motor real: exigir privWithoutMfa <= isentas para não mascarar um admin HUMANO sem
            //  MFA que se escondesse atrás do isolamento das contas OT — aqui o Stub mantém a regra do enunciado.)
            return ("NonCompliant", $"Stub: PR.AA reprovado — {privWithoutMfa:0} de {totalPriv:0} conta(s) privilegiada(s) do Entra ID sem MFA e SEM controle compensatório (isolamento de rede). Privilégio sem MFA é falha crítica (PoLP).");
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
    /// Decompõe as métricas do PROTECT no CHECKLIST técnico (<see cref="ComplianceCheck"/>) que justifica o
    /// veredito — um item por condição de auditoria, com o valor concreto que o decidiu. Reconhece o payload
    /// pelos MESMOS rótulos das regras de <see cref="EvaluateProtect"/>; devolve lista vazia fora do Protect.
    /// Vive à parte para não refatorar a tupla de retorno de todas as famílias — mesma varredura, outra saída.
    /// </summary>
    private static IReadOnlyList<ComplianceCheck> BuildProtectChecks(string p)
    {
        var checks = new List<ComplianceCheck>();

        // PR.AA — Identity & Access (telemetria de categoria)
        if (p.Contains("privileged mfa coverage:"))
        {
            var privMfa = Num(p, "privileged mfa coverage:");
            checks.Add(new("MFA Privilegiado Integral", privMfa >= 100, $"MFA em contas privilegiadas: {privMfa:0.#}% (exige 100%)."));
            checks.Add(new("Conditional Access Aplicado", Flag(p, "conditional access enforced:"), "Políticas de Conditional Access ativas no acesso."));
        }

        // PR.AA — Identity Posture (Entra ID)
        if (p.Contains("privileged accounts without mfa:"))
        {
            var without = Num(p, "privileged accounts without mfa:");
            var total = Num(p, "total privileged accounts:");
            checks.Add(new("Contas Privilegiadas com MFA", without <= 0, $"{without:0} de {total:0} contas privilegiadas sem MFA."));
            checks.Add(new("Isolamento de Rede (OT)", p.Contains("network isolation = true"), "Ativos sem MFA em rede isolada (controle compensatório)."));
        }

        // PR.DS — Data Security
        if (p.Contains("endpoint encryption coverage:"))
        {
            var enc = Num(p, "endpoint encryption coverage:");
            checks.Add(new("Endpoint Encrypted", enc >= 95, $"Criptografia de endpoint em {enc:0.#}% (mínimo 95%)."));
            checks.Add(new("No Unencrypted Traffic", !Flag(p, "unencrypted traffic detected:"), "Ausência de tráfego em claro na rede."));
        }

        // PR.PS — Platform Security
        if (p.Contains("cis benchmark compliance rate:"))
        {
            var cis = Num(p, "cis benchmark compliance rate:");
            var patches = Num(p, "missing critical patches:");
            checks.Add(new("CIS Hardening", cis >= 80, $"Conformidade CIS em {cis:0.#}% (mínimo 80%)."));
            checks.Add(new("No Critical Patches Pending", patches <= 0, $"{patches:0} patch(es) crítico(s) pendente(s)."));
        }

        // PR.IR — Infrastructure Resilience
        if (p.Contains("default deny firewall enforced:"))
            checks.Add(new("Default-Deny Firewall", Flag(p, "default deny firewall enforced:"), "Firewall com política default-deny (perímetro restritivo)."));

        return checks;
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

    /// <summary>
    /// Regras do pilar GOVERN (GV) avaliadas por TELEMETRIA (não por documento): governança tem métricas
    /// estruturadas, não só PDFs. Reconhecidas pelos rótulos que o <c>TelemetryIngestionService</c> compõe,
    /// com parsing dos valores. Retorna <c>null</c> quando o payload não é de Govern (segue o roteamento).
    /// </summary>
    private static (string Status, string Evidence)? EvaluateGovern(string p)
    {
        // GV.SC — Supply Chain Risk Mgmt: fornecedor de TI com acesso à rede SEM auditoria de terceiros ativa.
        if (p.Contains("suppliers with network access:") || p.Contains("third party audited:"))
        {
            var netAccessSuppliers = Num(p, "suppliers with network access:");
            var thirdPartyAudited = Flag(p, "third party audited:");
            return netAccessSuppliers > 0 && !thirdPartyAudited
                ? ("NonCompliant", $"Stub: GV.SC reprovado — {netAccessSuppliers:0} fornecedor(es) de TI com acesso à rede sem auditoria de terceiros ativa. Elo da cadeia de suprimentos não verificado.")
                : ("Compliant", "Stub: GV.SC conforme — fornecedores com acesso à rede sob auditoria de terceiros ativa (ou sem exposição de rede a terceiros).");
        }

        // GV.RR — Roles & Responsibilities: conta de administrador sem revisão periódica de acesso.
        if (p.Contains("admin accounts without periodic review:") || p.Contains("privileged access review configured:"))
        {
            var adminsWithoutReview = Num(p, "admin accounts without periodic review:");
            var reviewConfigured = Flag(p, "privileged access review configured:");
            return adminsWithoutReview > 0 || !reviewConfigured
                ? ("NonCompliant", $"Stub: GV.RR reprovado — {adminsWithoutReview:0} conta(s) de administrador sem revisão periódica, ou revisão de acesso privilegiado não configurada. Autoridade sem accountability.")
                : ("Compliant", "Stub: GV.RR conforme — contas de administrador sob revisão periódica de acesso configurada.");
        }

        // GV.RR — Identity Governance (telemetria do Entra ID): excesso de contas privilegiadas quebra o
        // princípio do menor privilégio (indicador "More than 10 Privileged Administrators exist"). Ancorado
        // no controle-alvo porque o MESMO retrato de identidade também alimenta PR.AA-01.
        if (TargetsControl(p, "gv.rr") && p.Contains("total privileged accounts:"))
        {
            var totalPriv = Num(p, "total privileged accounts:");
            return totalPriv > 10
                ? ("NonCompliant", $"Stub: GV.RR reprovado — {totalPriv:0} contas privilegiadas (>10) no Entra ID. Excesso de administradores quebra o menor privilégio e a governança de identidade.")
                : ("Compliant", $"Stub: GV.RR conforme — {totalPriv:0} contas privilegiadas (≤10), aderente ao menor privilégio.");
        }

        return null;
    }

    /// <summary>
    /// True se o payload MIRA o controle indicado (prefixo NIST, ex.: "pr.aa"/"gv.rr"). Lê o código-alvo do
    /// cabeçalho do User Prompt do avaliador ("NIST CSF 2.0 SUBCATEGORY: PR.AA-01") ou do envelope da
    /// telemetria de categoria ("(control PR.AA-01)"). Necessário porque UM MESMO retrato de identidade do
    /// Entra ID alimenta dois controles (PR.AA-01 e GV.RR-01): a âncora de código impede a regra de um de
    /// decidir o veredito do outro durante o roteamento.
    /// </summary>
    private static bool TargetsControl(string p, string codePrefix) =>
        p.Contains("subcategory: " + codePrefix) || p.Contains("(control " + codePrefix);

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
