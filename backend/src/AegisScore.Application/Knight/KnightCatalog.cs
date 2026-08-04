using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

/// <summary>
/// Desfecho determinístico da regra de UM indicador sobre os fatos normalizados: veredito, evidência factual,
/// quantidade de objetos afetados e — quando NotEvaluated — o motivo (dado/permissão ausente). Os metadados
/// (título, severidade, mapeamentos, recomendação) vêm da <see cref="KnightIndicatorDefinition"/>, não da regra.
/// </summary>
public sealed record KnightIndicatorOutcome(
    KnightIndicatorStatus Status,
    string Evidence,
    int AffectedObjectCount,
    string? NotEvaluatedReason = null);

/// <summary>
/// Definição versionada e ORIGINAL de um indicador do AEGIS KNIGHT. A regra opera sobre <see cref="KnightFactSet"/>
/// (fatos normalizados) — NÃO sobre um DTO de fornecedor. <see cref="Sources"/> declara quais fontes conseguem
/// avaliar o indicador; é isso que torna o catálogo multicoletor. Textos autorais — nada copiado de terceiros.
/// </summary>
public sealed record KnightIndicatorDefinition(
    string Id,
    string Version,
    string Title,
    KnightIndicatorCategory Category,
    SeverityLevel Severity,
    IReadOnlySet<KnightSourceType> Sources,
    IReadOnlyList<string> NistCodes,
    IReadOnlyList<string> MitreTechniques,
    string Recommendation,
    string ExpectedEvidence,
    Func<KnightFactSet, KnightIndicatorOutcome> Rule);

/// <summary>Indicador já avaliado: a definição (metadados) + o desfecho determinístico dos fatos.</summary>
public sealed record KnightEvaluatedIndicator(
    KnightIndicatorDefinition Definition,
    KnightIndicatorStatus Status,
    string Evidence,
    int AffectedObjectCount,
    string? NotEvaluatedReason = null);

/// <summary>
/// Catálogo ORIGINAL e VERSIONADO do AEGIS KNIGHT. Indicadores multicoletor: cada um declara as fontes que o
/// avaliam e uma regra determinística sobre fatos normalizados. Limiares CENTRALIZADOS aqui — nunca espalhados
/// pelo controller, coletor ou frontend. Textos próprios; nenhum nome, descrição, fórmula ou UUID de terceiro.
/// </summary>
public static class KnightCatalog
{
    /// <summary>Versão do catálogo — carimbada na execução para rastreabilidade do veredito.</summary>
    public const string Version = "ak-knight-v1";

    // ---- Limiares centralizados (única fonte da verdade dos números da regra) ----

    /// <summary>Teto de contas privilegiadas aceitável pelo menor privilégio (acima disto, exposição).</summary>
    public const int MaxPrivilegedAccounts = 10;

    /// <summary>Janela (dias) que qualifica um convidado como inativo.</summary>
    public const int InactiveGuestWindowDays = 30;

    /// <summary>Cobertura mínima aceitável de registro de MFA (%).</summary>
    public const double MinMfaCoveragePercent = 90;

    /// <summary>Janela (dias) para credenciais de aplicação "vencendo" (usada pelo coletor).</summary>
    public const int AppCredentialExpiryWindowDays = 30;

    /// <summary>Janela (dias) sem atividade que marca uma conta privilegiada como obsoleta (usada pelo coletor).</summary>
    public const int StalePrivilegedWindowDays = 60;

    // Indicadores de identidade AGNÓSTICOS de fonte: valem para Demo, Entra e Google Workspace (cada coletor
    // produz os mesmos fatos normalizados). É o que permite um novo coletor entrar no pipeline SEM tocar o núcleo.
    private static readonly IReadOnlySet<KnightSourceType> SharedSources =
        new HashSet<KnightSourceType> { KnightSourceType.Demo, KnightSourceType.MicrosoftEntraId, KnightSourceType.GoogleWorkspace };

    private static readonly IReadOnlySet<KnightSourceType> EntraOnly =
        new HashSet<KnightSourceType> { KnightSourceType.MicrosoftEntraId };

    // ---- Fábricas de desfecho ------------------------------------------------------------------------

    private static KnightIndicatorOutcome Passed(string evidence) =>
        new(KnightIndicatorStatus.Passed, evidence, 0);

    private static KnightIndicatorOutcome Exposed(string evidence, int affected) =>
        new(KnightIndicatorStatus.Exposed, evidence, affected);

    private static KnightIndicatorOutcome Mitigated(string evidence, int affected) =>
        new(KnightIndicatorStatus.Mitigated, evidence, affected);

    private static KnightIndicatorOutcome NotEvaluated(string reason) =>
        new(KnightIndicatorStatus.NotEvaluated, $"Não avaliado: {reason}", 0, reason);

    private static string Fmt(long? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "?";

    /// <summary>
    /// Lê uma observação de contagem: se não coletada, curto-circuita em NotEvaluated (a ausência NUNCA vira
    /// aprovação). Devolve o desfecho de saída via <paramref name="notEvaluated"/> e a contagem via out.
    /// </summary>
    private static bool TryCount(KnightFactSet f, KnightSignalKey key, out long count, out KnightIndicatorOutcome notEvaluated)
    {
        var obs = f.Get(key);
        if (!obs.IsCollected)
        {
            count = 0;
            notEvaluated = NotEvaluated(obs.MissingReason ?? "dado ausente");
            return false;
        }
        count = obs.Count ?? 0;
        notEvaluated = null!;
        return true;
    }

    private static bool TryFlag(KnightFactSet f, KnightSignalKey key, out bool flag, out KnightIndicatorOutcome notEvaluated)
    {
        var obs = f.Get(key);
        if (!obs.IsCollected)
        {
            flag = false;
            notEvaluated = NotEvaluated(obs.MissingReason ?? "dado ausente");
            return false;
        }
        flag = obs.Flag ?? false;
        notEvaluated = null!;
        return true;
    }

    private static bool TryRatio(KnightFactSet f, KnightSignalKey key, out double ratio, out KnightIndicatorOutcome notEvaluated)
    {
        var obs = f.Get(key);
        if (!obs.IsCollected)
        {
            ratio = 0;
            notEvaluated = NotEvaluated(obs.MissingReason ?? "dado ausente");
            return false;
        }
        ratio = obs.Ratio ?? 0;
        notEvaluated = null!;
        return true;
    }

    /// <summary>Todos os indicadores (multicoletor). Ordem estável = ordem de exibição por padrão.</summary>
    public static IReadOnlyList<KnightIndicatorDefinition> Indicators { get; } = new[]
    {
        // ==== Compartilhados: Demo (sintético) e Entra (real) ====

        new KnightIndicatorDefinition(
            "AK-ENTRA-001", "1", "Contas privilegiadas sem autenticação multifator efetiva",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Critical, SharedSources,
            new[] { "PR.AA-01", "PR.AA-03" }, new[] { "T1078 · Valid Accounts", "T1078.004 · Cloud Accounts" },
            "Exigir MFA resistente a phishing em todas as contas privilegiadas e bloquear o acesso privilegiado sem segundo fator.",
            "Número de contas privilegiadas cuja verificação de segundo fator não está efetivamente aplicada.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.PrivilegedAccountsWithoutMfa, out var without, out var ne)) return ne;
                var total = f.Get(KnightSignalKey.PrivilegedAccountsTotal);
                var totalTxt = total.IsCollected ? Fmt(total.Count) : "?";
                return without > 0
                    ? Exposed($"{without} de {totalTxt} conta(s) privilegiada(s) sem MFA efetivo.", (int)without)
                    : Passed($"Todas as {totalTxt} conta(s) privilegiada(s) com MFA efetivo.");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-002", "1", "Volume excessivo de contas privilegiadas",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.High, SharedSources,
            new[] { "PR.AA-05", "GV.RR-02" }, new[] { "T1078 · Valid Accounts" },
            "Reduzir o número de contas privilegiadas ao mínimo necessário e adotar elevação just-in-time.",
            $"Total de contas privilegiadas comparado ao teto de menor privilégio ({MaxPrivilegedAccounts}).",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.PrivilegedAccountsTotal, out var total, out var ne)) return ne;
                return total > MaxPrivilegedAccounts
                    ? Exposed($"{total} contas privilegiadas excedem o teto de menor privilégio ({MaxPrivilegedAccounts}).", (int)total)
                    : Passed($"{total} contas privilegiadas dentro do teto de menor privilégio ({MaxPrivilegedAccounts}).");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-003", "1", "Contas privilegiadas com caixa de correio ativa",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Medium, SharedSources,
            new[] { "PR.AA-01" }, new[] { "T1566 · Phishing" },
            "Separar contas administrativas de caixas de correio; usar identidades dedicadas sem e-mail para tarefas privilegiadas.",
            "Número de contas privilegiadas que possuem caixa de correio ativa.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.PrivilegedAccountsWithMailbox, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} conta(s) privilegiada(s) com caixa de correio ativa — superfície de phishing sobre o administrador.", (int)n)
                    : Passed("Nenhuma conta privilegiada com caixa de correio ativa.");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-004", "1", "Contas de convidado inativas além da janela definida",
            KnightIndicatorCategory.GuestAccess, SeverityLevel.Medium, SharedSources,
            new[] { "PR.AA-01", "GV.RR-02" }, Array.Empty<string>(),
            "Revisar e desativar contas de convidado sem uso além da janela; automatizar a expiração de acesso de terceiros.",
            $"Número de convidados inativos há mais de {InactiveGuestWindowDays} dias.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.InactiveGuestAccounts, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} convidado(s) inativo(s) há mais de {InactiveGuestWindowDays} dias — acesso de terceiros esquecido.", (int)n)
                    : Passed($"Nenhum convidado inativo além de {InactiveGuestWindowDays} dias.");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-005", "1", "Contas técnicas isentas de MFA sem controle compensatório comprovado",
            KnightIndicatorCategory.ServiceAccounts, SeverityLevel.High, SharedSources,
            new[] { "PR.AA-01" }, new[] { "T1078 · Valid Accounts" },
            "Migrar contas de serviço para identidades gerenciadas/credenciais rotacionadas; comprovar tecnicamente qualquer isenção por controle compensatório.",
            "Contas técnicas/de serviço isentas de MFA e a presença (ou não) de um controle compensatório COMPROVADO.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.ServiceAccountsMfaExempt, out var count, out var ne)) return ne;
                if (count == 0) return Passed("Nenhuma conta técnica isenta de MFA.");
                var proven = f.Get(KnightSignalKey.ServiceAccountMfaExemptionProven);
                var isProven = proven.IsCollected && proven.Flag == true;
                return isProven
                    ? Mitigated($"{count} conta(s) técnica(s) isenta(s) de MFA, com controle compensatório tecnicamente comprovado.", (int)count)
                    : Exposed($"{count} conta(s) técnica(s) isenta(s) de MFA SEM controle compensatório comprovado.", (int)count);
            }),

        // ==== Específicos da coleta real de diretório (Entra) ====

        new KnightIndicatorDefinition(
            "AK-ENTRA-006", "1", "Baixa cobertura de registro de MFA",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.High, EntraOnly,
            new[] { "PR.AA-01", "PR.AA-02" }, Array.Empty<string>(),
            "Impor o registro de MFA a todos os usuários habilitados e monitorar a cobertura por campanha.",
            $"Percentual de usuários com MFA registrado/capaz comparado ao mínimo de {MinMfaCoveragePercent}%.",
            f =>
            {
                if (!TryRatio(f, KnightSignalKey.MfaRegistrationCoveragePercent, out var pct, out var ne)) return ne;
                return pct < MinMfaCoveragePercent
                    ? Exposed($"Cobertura de registro de MFA em {pct.ToString("0.#", CultureInfo.InvariantCulture)}% (mínimo {MinMfaCoveragePercent}%).", 0)
                    : Passed($"Cobertura de registro de MFA em {pct.ToString("0.#", CultureInfo.InvariantCulture)}%.");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-007", "1", "Autenticação legada não bloqueada por política",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.High, EntraOnly,
            new[] { "PR.AA-02", "PR.PS-01" }, new[] { "T1110 · Brute Force" },
            "Bloquear protocolos de autenticação legada por política de acesso condicional ou security defaults.",
            "Existência de política que bloqueia clientes de autenticação legada — OU security defaults habilitados.",
            // Security Defaults habilitados bloqueiam a autenticação legada; participam do veredito. Sem evidência
            // suficiente (nenhum dos sinais coletado) → NotEvaluated; presente e negativo → Exposed; nunca Passed sem prova.
            f =>
            {
                var defaults = f.Get(KnightSignalKey.SecurityDefaultsEnabled);
                var blocked = f.Get(KnightSignalKey.LegacyAuthenticationBlocked);
                if (defaults.IsCollected && defaults.Flag == true)
                    return Passed("Autenticação legada bloqueada pelos security defaults.");
                if (blocked.IsCollected && blocked.Flag == true)
                    return Passed("Autenticação legada bloqueada por política de acesso condicional.");
                if (blocked.IsCollected || defaults.IsCollected)
                    return Exposed("Autenticação legada NÃO comprovadamente bloqueada (sem política dedicada nem security defaults) — canal sem MFA aberto.", 0);
                return NotEvaluated(blocked.MissingReason ?? "sinal de bloqueio de autenticação legada não coletado");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-008", "1", "Ausência de política de MFA para acesso administrativo",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High, EntraOnly,
            new[] { "PR.AA-01", "PR.AA-03" }, Array.Empty<string>(),
            "Criar política de acesso condicional exigindo MFA (idealmente resistente a phishing) para papéis administrativos.",
            "Existência de política exigindo MFA para acesso administrativo — OU security defaults habilitados.",
            // Security Defaults habilitados impõem MFA administrativa; participam do veredito. Uma política parcial
            // (papel único, exclusões amplas, escopo estreito) já NÃO marca AdminMfaPolicyEnforced no coletor.
            f =>
            {
                var defaults = f.Get(KnightSignalKey.SecurityDefaultsEnabled);
                var adminMfa = f.Get(KnightSignalKey.AdminMfaPolicyEnforced);
                if (defaults.IsCollected && defaults.Flag == true)
                    return Passed("MFA administrativa imposta pelos security defaults.");
                if (adminMfa.IsCollected && adminMfa.Flag == true)
                    return Passed("MFA exigida para acesso administrativo por política de acesso condicional.");
                if (adminMfa.IsCollected || defaults.IsCollected)
                    return Exposed("Sem política de MFA administrativa comprovada e sem security defaults.", 0);
                return NotEvaluated(adminMfa.MissingReason ?? "sinal de MFA administrativa não coletado");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-009", "1", "Credenciais de aplicação vencidas ou vencendo",
            KnightIndicatorCategory.AccountHygiene, SeverityLevel.Medium, EntraOnly,
            new[] { "PR.AA-01", "ID.AM-02" }, Array.Empty<string>(),
            "Rotacionar credenciais de aplicação antes do vencimento e preferir certificados/identidades gerenciadas a segredos.",
            $"Número de segredos/certificados de aplicação vencidos ou vencendo em até {AppCredentialExpiryWindowDays} dias.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.ApplicationCredentialsExpiring, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} credencial(is) de aplicação vencida(s) ou vencendo em até {AppCredentialExpiryWindowDays} dias.", (int)n)
                    : Passed("Nenhuma credencial de aplicação vencida ou próxima do vencimento.");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-010", "1", "Aplicações com permissões de alto privilégio CONCEDIDAS sobre o diretório",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.High, EntraOnly,
            new[] { "PR.AA-05", "GV.RR-02" }, new[] { "T1098 · Account Manipulation" },
            "Revisar e reduzir permissões de aplicativo de alto privilégio; aplicar consentimento granular e menor privilégio a aplicações.",
            // Mede permissões de aplicativo EFETIVAMENTE CONCEDIDAS (appRoleAssignments no service principal do
            // Graph) — não requiredResourceAccess, que é apenas o que a aplicação DECLARA/solicita.
            "Número de aplicações com permissões de aplicativo de alto privilégio EFETIVAMENTE CONCEDIDAS (appRoleAssignments) sobre o diretório.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.HighPrivilegeApplications, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} aplicação(ões) com permissões de aplicativo de alto privilégio CONCEDIDAS sobre o diretório.", (int)n)
                    : Passed("Nenhuma aplicação com permissões de aplicativo de alto privilégio concedidas.");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-011", "1", "Contas privilegiadas sem atividade recente",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Medium, EntraOnly,
            new[] { "PR.AA-01", "GV.RR-02" }, new[] { "T1078 · Valid Accounts" },
            "Revisar e remover/desativar acesso privilegiado sem uso recente; adotar acesso privilegiado just-in-time.",
            $"Número de contas privilegiadas sem atividade nos últimos {StalePrivilegedWindowDays} dias.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.StalePrivilegedAccounts, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} conta(s) privilegiada(s) sem atividade nos últimos {StalePrivilegedWindowDays} dias.", (int)n)
                    : Passed("Nenhuma conta privilegiada obsoleta.");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-012", "1", "Membros externos em papéis privilegiados",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High, EntraOnly,
            new[] { "PR.AA-05", "GV.SC-04" }, new[] { "T1078.004 · Cloud Accounts" },
            "Restringir papéis privilegiados a identidades internas; usar acesso de convidado com aprovação e revisão de acesso.",
            "Número de membros externos (convidados) em papéis privilegiados.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.ExternalMembersInPrivilegedRoles, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} membro(s) externo(s) em papéis privilegiados.", (int)n)
                    : Passed("Nenhum membro externo em papéis privilegiados.");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-013", "1", "Aplicações com consentimento DELEGADO amplo (tenant-wide)",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.Medium, EntraOnly,
            new[] { "GV.SC-04", "PR.AA-05" }, Array.Empty<string>(),
            "Revisar consentimentos delegados concedidos a todos os usuários; restringir o consentimento do usuário e reavaliar aplicações de terceiros.",
            // Consentimento DELEGADO concedido para TODOS os usuários (oauth2PermissionGrants, consentType=AllPrincipals).
            // Consentimento "Principal" (para um único usuário) NÃO entra no total tenant-wide.
            "Número de aplicações com consentimento DELEGADO concedido a todos os usuários (consentType=AllPrincipals).",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.AdminConsentedApplications, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} aplicação(ões) com consentimento delegado tenant-wide (concedido a todos os usuários).", (int)n)
                    : Passed("Nenhuma aplicação com consentimento delegado tenant-wide.");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-014", "1", "Ausência de baseline de segurança da plataforma",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.Medium, EntraOnly,
            new[] { "PR.PS-01", "GV.RR-02" }, Array.Empty<string>(),
            "Habilitar os security defaults ou manter um baseline equivalente de acesso condicional.",
            "Security defaults habilitados OU política de MFA administrativa presente.",
            f =>
            {
                var defaults = f.Get(KnightSignalKey.SecurityDefaultsEnabled);
                var adminMfa = f.Get(KnightSignalKey.AdminMfaPolicyEnforced);
                if (!defaults.IsCollected && !adminMfa.IsCollected)
                    return NotEvaluated(defaults.MissingReason ?? "baseline de segurança não coletado");
                var hasBaseline = (defaults.IsCollected && defaults.Flag == true)
                    || (adminMfa.IsCollected && adminMfa.Flag == true);
                return hasBaseline
                    ? Passed("Baseline de segurança presente (security defaults ou política de MFA administrativa).")
                    : Exposed("Sem security defaults e sem política de MFA administrativa — baseline ausente.", 0);
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-015", "1", "Contas de emergência (break-glass) não designadas",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Medium, EntraOnly,
            new[] { "PR.AA-01", "RC.RP-01" }, Array.Empty<string>(),
            "Designar e proteger contas de emergência (break-glass) e monitorar seu uso.",
            "Contas de emergência/break-glass designadas — tipicamente requer marcação manual, não inferível por API.",
            // Sem sinal de designação → NotEvaluated com motivo (demonstra: falta de dado ≠ aprovação),
            // mesmo numa coleta concluída.
            f =>
            {
                if (!TryCount(f, KnightSignalKey.DesignatedBreakGlassAccounts, out var n, out var ne)) return ne;
                return n > 0
                    ? Passed($"{n} conta(s) de emergência designada(s).")
                    : Exposed("Nenhuma conta de emergência (break-glass) designada.", 0);
            }),
    };

    /// <summary>Indicadores aplicáveis a uma fonte (o "perfil" de assessment daquela fonte).</summary>
    public static IReadOnlyList<KnightIndicatorDefinition> ForSource(KnightSourceType source) =>
        Indicators.Where(d => d.Sources.Contains(source)).ToList();
}

/// <summary>
/// Motor determinístico PURO: aplica as regras dos indicadores APLICÁVEIS à fonte sobre os fatos normalizados.
/// Sem EF, sem rede, sem IA — testável isoladamente. Uma regra que lance vira <see cref="KnightIndicatorStatus.Error"/>
/// (nunca aprova por falha, nunca derruba as demais). Dado ausente → NotEvaluated (reduz cobertura, não aprova).
/// </summary>
public static class KnightIndicatorEvaluator
{
    public static IReadOnlyList<KnightEvaluatedIndicator> Evaluate(KnightFactSet facts, KnightSourceType source)
    {
        var applicable = KnightCatalog.ForSource(source);
        var results = new List<KnightEvaluatedIndicator>(applicable.Count);
        foreach (var def in applicable)
        {
            try
            {
                var outcome = def.Rule(facts);
                results.Add(new KnightEvaluatedIndicator(
                    def, outcome.Status, outcome.Evidence, outcome.AffectedObjectCount, outcome.NotEvaluatedReason));
            }
            catch (Exception ex)
            {
                results.Add(new KnightEvaluatedIndicator(
                    def, KnightIndicatorStatus.Error,
                    $"Não foi possível avaliar o indicador: {ex.GetType().Name}.", 0,
                    "Erro ao avaliar a regra do indicador."));
            }
        }
        return results;
    }
}
