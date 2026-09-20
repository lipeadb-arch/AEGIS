using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
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
    Func<KnightFactSet, KnightIndicatorOutcome> Rule)
{
    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-01] Serviço onde a configuração avaliada vive. <see cref="KnightService.Unspecified"/>
    /// nos controles compartilhados entre fontes: o serviço vem da fonte avaliada.
    /// </summary>
    public KnightService Service { get; init; } = KnightService.Unspecified;

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-01] Controles do catálogo de referência que este controle avalia, com o grau de
    /// equivalência. Declarar não basta para contar como implementado: a cobertura também exige a regra no fluxo
    /// ativo e a coleta que ela consome (ver <see cref="KnightReferenceCatalog"/>).
    /// </summary>
    public IReadOnlyList<KnightReferenceLink> References { get; init; } = Array.Empty<KnightReferenceLink>();

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-01] Regra de CONFIGURAÇÃO: lê a coleta relida do ADM e devolve o veredito com os
    /// objetos que o sustentam. Quando presente, é ela que decide; <see cref="Rule"/> fica só para o caminho sem
    /// contexto de coleta, em que o controle é declarado não avaliado.
    /// </summary>
    public Func<KnightEvaluationContext, KnightControlOutcome>? Evaluate { get; init; }
}

/// <summary>Indicador já avaliado: a definição (metadados) + o desfecho determinístico dos fatos.</summary>
/// <remarks>
/// [AEGIS-KNIGHT-COVERAGE-01] Um controle de configuração devolve também os objetos que sustentam o veredito:
/// <paramref name="Objects"/> (afetados e evidências), a completude da lista de afetados e a limitação declarada.
/// <c>null</c> nos controles de fatos agregados, cujo detalhe continua vindo dos conjuntos da coleta.
/// </remarks>
public sealed record KnightEvaluatedIndicator(
    KnightIndicatorDefinition Definition,
    KnightIndicatorStatus Status,
    string Evidence,
    int AffectedObjectCount,
    string? NotEvaluatedReason = null,
    IReadOnlyList<KnightIndicatorObject>? Objects = null,
    bool ObjectsComplete = true,
    string? ObjectsLimitation = null);

/// <summary>
/// Catálogo ORIGINAL e VERSIONADO do AEGIS KNIGHT. Indicadores multicoletor: cada um declara as fontes que o
/// avaliam e uma regra determinística sobre fatos normalizados. Limiares CENTRALIZADOS aqui — nunca espalhados
/// pelo controller, coletor ou frontend. Textos próprios; nenhum nome, descrição, fórmula ou UUID de terceiro.
/// </summary>
public static class KnightCatalog
{
    /// <summary>Versão do catálogo — carimbada na execução para rastreabilidade do veredito.</summary>
    // v2: acrescenta os indicadores GoogleOnly `AK-GWS-001..006` (coletor real Google Workspace). Os
    // indicadores compartilhados e os do Entra permanecem inalterados.
    // v3 [AEGIS-KNIGHT-MULTICLOUD-01]: AK-ENTRA-001 passa a afirmar só o que mede (método REGISTRADO); AK-ENTRA-007,
    // 008 e 014 leem as políticas de acesso condicional por estado, alvo (inclusive papel), aplicação, condição e
    // controle exigido, e deixam de reprovar quando a alternativa (security defaults) não pôde ser verificada.
    // Exclusões explícitas nunca viram cobertura: sem outra política que as cubra, 007/008 ficam não avaliados
    // (a exceção não é irregular por si, mas não comprova proteção); 014 exige alcance declarado amplo.
    // Comparações entre fotografias v2 e v3 são recusadas pelo comparador (versão de catálogo diferente).
    // v4 [AEGIS-KNIGHT-COVERAGE-01]: acrescenta os controles de CONFIGURAÇÃO do Microsoft Entra ID (AK-ENTRA-016 em
    // diante), que leem a configuração do locatário relida do ADM, e declara em cada controle o serviço e os
    // controles de referência que ele avalia. Os critérios e limiares de AK-ENTRA-001..015 e AK-GWS-001..006 NÃO
    // mudam — só ganham referências. Mudar o conjunto de controles muda o denominador da nota e da cobertura: por
    // isso a versão sobe, fotografias v3 continuam com o catálogo v3 congelado e o comparador recusa v3 × v4.
    public const string Version = "ak-knight-v4";

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

    /// <summary>Cobertura mínima aceitável de 2SV (verificação em duas etapas) do Google Workspace (%).</summary>
    public const double MinTwoStepVerificationCoveragePercent = 90;

    /// <summary>Janela (dias) de auditoria recente do Google Workspace (compartilhamento externo, OAuth).</summary>
    public const int GoogleAuditWindowDays = 7;

    // Indicadores de identidade AGNÓSTICOS de fonte: valem para Demo, Entra e Google Workspace (cada coletor
    // produz os mesmos fatos normalizados). É o que permite um novo coletor entrar no pipeline SEM tocar o núcleo.
    private static readonly IReadOnlySet<KnightSourceType> SharedSources =
        new HashSet<KnightSourceType> { KnightSourceType.Demo, KnightSourceType.MicrosoftEntraId, KnightSourceType.GoogleWorkspace };

    private static readonly IReadOnlySet<KnightSourceType> EntraOnly =
        new HashSet<KnightSourceType> { KnightSourceType.MicrosoftEntraId };

    // Indicadores exclusivos do Google Workspace (fatos que só o coletor do Google produz).
    private static readonly IReadOnlySet<KnightSourceType> GoogleOnly =
        new HashSet<KnightSourceType> { KnightSourceType.GoogleWorkspace };

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
    public static IReadOnlyList<KnightIndicatorDefinition> Indicators { get; } =
        BaseIndicators().Concat(EntraConfigurationControls.Definitions).ToList();

    private const string M365Ref = "CIS-M365-7.0.0:";
    private const string AzRef = "CIS-AZ-6.0.0:";

    /// <summary>
    /// [v4] Referências dos indicadores originais. Critério e limiar continuam os mesmos; a equivalência com a
    /// referência é declarada com a diferença explícita quando não é idêntica.
    /// </summary>
    private static IReadOnlyDictionary<string, KnightReferenceLink[]> BaseReferences() => new Dictionary<string, KnightReferenceLink[]>(StringComparer.Ordinal)
    {
        ["AK-ENTRA-006"] = new[]
        {
            new KnightReferenceLink(M365Ref + "5.2.3.4", KnightReferenceMatch.Partial,
                $"A referência pede que todos os usuários membros sejam capazes de MFA; o AEGIS reprova abaixo de {MinMfaCoveragePercent}% de cobertura de registro sobre os usuários do relatório de registro."),
        },
        ["AK-ENTRA-007"] = new[] { new KnightReferenceLink(M365Ref + "5.2.2.3") },
        ["AK-ENTRA-008"] = new[]
        {
            new KnightReferenceLink(M365Ref + "5.2.2.1", KnightReferenceMatch.Partial,
                "O AEGIS verifica todos os papéis de diretório ativos com membros; a referência enumera um conjunto de papéis administrativos."),
        },
        ["AK-ENTRA-014"] = new[]
        {
            new KnightReferenceLink(M365Ref + "5.2.2.2", KnightReferenceMatch.Partial,
                "O AEGIS aprova com security defaults ou com política habilitada de alvo declarado em todos os usuários; exclusões são listadas como evidência e a cobertura conta a conta de administradores é avaliada no AK-ENTRA-008."),
            new KnightReferenceLink(AzRef + "5.1.1", KnightReferenceMatch.Partial,
                "A referência pede security defaults quando não há acesso condicional; o AEGIS aceita qualquer uma das duas bases."),
        },
    };

    private static KnightIndicatorDefinition WithBaseReferences(KnightIndicatorDefinition d, IReadOnlyDictionary<string, KnightReferenceLink[]> map) =>
        map.TryGetValue(d.Id, out var refs) ? d with { References = refs } : d;

    // Indicadores originais (v1–v3). A ordem é preservada; a v4 só acrescenta referências.
    private static IEnumerable<KnightIndicatorDefinition> BaseIndicators()
    {
        var map = BaseReferences();
        return RawBaseIndicators().Select(d => WithBaseReferences(d, map));
    }

    private static IEnumerable<KnightIndicatorDefinition> RawBaseIndicators() => new[]
    {
        // ==== Compartilhados: Demo (sintético) e Entra (real) ====

        new KnightIndicatorDefinition(
            // [v3] O título dizia "sem MFA efetiva", mas a regra observa REGISTRO de método. Afirmar exigência ou
            // aplicação efetiva exigiria outras fontes — a exigência por política é o AK-ENTRA-008.
            "AK-ENTRA-001", "2", "Contas privilegiadas sem método de MFA registrado",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Critical, SharedSources,
            new[] { "PR.AA-01", "PR.AA-03" }, new[] { "T1078 · Valid Accounts", "T1078.004 · Cloud Accounts" },
            "Exigir MFA resistente a phishing em todas as contas privilegiadas e bloquear o acesso privilegiado sem segundo fator.",
            "Número de contas privilegiadas sem método capaz de MFA registrado no relatório de registro do diretório.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.PrivilegedAccountsWithoutMfa, out var without, out var ne)) return ne;
                var total = f.Get(KnightSignalKey.PrivilegedAccountsTotal);
                var totalTxt = total.IsCollected ? Fmt(total.Count) : "?";
                // [v4] O total inclui aplicações e grupos com papel (não são contas de pessoa): o texto diz
                // "identidades" para o total e "contas" só para o que o relatório de registro cobre.
                return without > 0
                    ? Exposed($"{without} conta(s) com papel privilegiado sem método capaz de MFA registrado, entre {totalTxt} identidade(s) privilegiada(s).", (int)without)
                    : Passed($"Nenhuma conta com papel privilegiado sem método capaz de MFA registrado, entre {totalTxt} identidade(s) privilegiada(s) (registro não comprova exigência por política).");
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-002", "1", "Volume excessivo de identidades privilegiadas",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.High, SharedSources,
            new[] { "PR.AA-05", "GV.RR-02" }, new[] { "T1078 · Valid Accounts" },
            "Reduzir o número de contas privilegiadas ao mínimo necessário e adotar elevação just-in-time.",
            $"Total de identidades com papel privilegiado (contas, convidados, aplicações e grupos) comparado ao teto de menor privilégio ({MaxPrivilegedAccounts}).",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.PrivilegedAccountsTotal, out var total, out var ne)) return ne;
                // [v4] A população são identidades com papel (contas, convidados, aplicações, grupos) — não só contas.
                return total > MaxPrivilegedAccounts
                    ? Exposed($"{total} identidades com papel privilegiado excedem o teto de menor privilégio ({MaxPrivilegedAccounts}); a composição por tipo está na lista.", (int)total)
                    : Passed($"{total} identidades com papel privilegiado, dentro do teto de menor privilégio ({MaxPrivilegedAccounts}).");
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
            // [AEGIS-MVP-PRODUCT-03] O título dizia "inativas". A regra só observa AUSÊNCIA DE SINAL de acesso
            // na janela — que pode ser desuso real ou simplesmente falta de registro disponível. O critério e o
            // limiar são os MESMOS (a versão do catálogo não muda); só a afirmação foi trazida de volta ao que
            // a coleta prova. Avaliações já gravadas mantêm o texto original, exibido como literal do histórico.
            "AK-ENTRA-004", "1", "Contas de convidado sem sinal de acesso na janela definida",
            KnightIndicatorCategory.GuestAccess, SeverityLevel.Medium, SharedSources,
            new[] { "PR.AA-01", "GV.RR-02" }, Array.Empty<string>(),
            // [AEGIS-MVP-PRODUCT-03] A primeira ação deixou de começar por "desativar". A regra observa AUSÊNCIA
            // DE SINAL de acesso, que não é o mesmo que desuso comprovado: desativar antes de confirmar troca
            // um risco por uma interrupção de serviço, com base numa inatividade que a coleta não provou.
            "Confirmar com a área responsável se o acesso de cada convidado ainda é necessário e desativar somente os que forem confirmados como dispensáveis; automatizar a expiração do acesso de terceiros.",
            $"Número de convidados sem sinal de acesso registrado nos últimos {InactiveGuestWindowDays} dias.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.InactiveGuestAccounts, out var n, out var ne)) return ne;
                return n > 0
                    // "Acesso de terceiros esquecido" afirmava desuso a partir de ausência de registro. O texto
                    // gravado passa a dizer o que a regra REALMENTE observou; as avaliações já gravadas mantêm o
                    // texto original, que continua exibido como literal do histórico.
                    ? Exposed($"{n} convidado(s) sem sinal de acesso registrado nos últimos {InactiveGuestWindowDays} dias.", (int)n)
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
            "AK-ENTRA-007", "2", "Autenticação legada não bloqueada por política",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.High, EntraOnly,
            new[] { "PR.AA-02", "PR.PS-01" }, new[] { "T1110 · Brute Force" },
            "Bloquear os clientes de autenticação legada (Exchange ActiveSync e outros clientes) por política de acesso condicional habilitada para todos os usuários e aplicações, ou habilitar os security defaults.",
            "Política HABILITADA bloqueando Exchange ActiveSync e outros clientes (ou todos os tipos de cliente) para todos os usuários e aplicações, sem exclusão que outra política não cubra — OU security defaults habilitados.",
            // [v2] Reprovar exige as DUAS verificações: nenhuma política bloqueia E os security defaults estão
            // comprovadamente desligados. Security defaults não verificado → não há como reprovar. Bloqueio "para
            // todos" com exclusões não cobertas por outra política → sinal ausente com o motivo → não avaliado.
            f =>
            {
                var defaults = f.Get(KnightSignalKey.SecurityDefaultsEnabled);
                var blocked = f.Get(KnightSignalKey.LegacyAuthenticationBlocked);
                if (defaults.IsCollected && defaults.Flag == true)
                    return Passed("Autenticação legada bloqueada pelos security defaults.");
                if (blocked.IsCollected && blocked.Flag == true)
                    return Passed("Autenticação legada bloqueada por política de acesso condicional habilitada, para todos os usuários e aplicações — sem exclusão que outra política não cubra.");
                if (!blocked.IsCollected)
                    return NotEvaluated(blocked.MissingReason ?? "políticas de acesso condicional não coletadas");
                if (!defaults.IsCollected)
                    return NotEvaluated("nenhuma política habilitada bloqueia a autenticação legada, mas os security defaults não puderam ser verificados — "
                        + (defaults.MissingReason ?? "sinal não coletado"));
                return Exposed("Nenhuma política habilitada bloqueia a autenticação legada para todos os usuários e aplicações, e os security defaults estão desligados.", 0);
            }),

        new KnightIndicatorDefinition(
            "AK-ENTRA-008", "2", "Papéis administrativos sem exigência de MFA por política",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High, EntraOnly,
            new[] { "PR.AA-01", "PR.AA-03" }, Array.Empty<string>(),
            "Criar ou ajustar política de acesso condicional habilitada que exija MFA (idealmente resistente a phishing) para os papéis administrativos listados, em todas as aplicações, excluindo apenas as contas de emergência.",
            "Cada membro dos papéis privilegiados ativos alcançado por política HABILITADA que exige MFA em todas as aplicações, sem condição que a estreite e sem exclusão que outra política não cubra — OU security defaults habilitados.",
            // [v2] Lê as políticas membro a membro (ver ConditionalAccessAnalyzer). Cobertura não resolvida (grupo)
            // ou exceções explícitas sem outra cobertura → não avaliado; security defaults não verificado → não reprova.
            f =>
            {
                var defaults = f.Get(KnightSignalKey.SecurityDefaultsEnabled);
                var uncovered = f.Get(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy);
                if (defaults.IsCollected && defaults.Flag == true)
                    return Passed("MFA administrativa imposta pelos security defaults.");
                if (!uncovered.IsCollected)
                    return NotEvaluated(uncovered.MissingReason ?? "cobertura de MFA administrativa não coletada");
                var n = uncovered.Count ?? 0;
                if (n == 0)
                    return Passed("Todos os membros dos papéis privilegiados ativos são alcançados por política habilitada que exige MFA em todas as aplicações, sem exclusão que os deixe de fora.");
                if (!defaults.IsCollected)
                    return NotEvaluated($"{n} papel(éis) privilegiado(s) sem política que exija MFA, mas os security defaults não puderam ser verificados — "
                        + (defaults.MissingReason ?? "sinal não coletado"));
                return Exposed($"{n} papel(éis) privilegiado(s) ativo(s) com membro(s) sem política habilitada que exija MFA em todas as aplicações (os membros estão nomeados na evidência), e os security defaults estão desligados.", (int)n);
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
            "AK-ENTRA-014", "2", "Ausência de baseline de segurança da plataforma",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.Medium, EntraOnly,
            new[] { "PR.PS-01", "GV.RR-02" }, Array.Empty<string>(),
            "Habilitar os security defaults ou manter uma política de acesso condicional habilitada exigindo MFA de todos os usuários, em todas as aplicações.",
            "Security defaults habilitados OU ao menos uma política HABILITADA exigindo MFA ou força de autenticação com alvo declarado em todos os usuários, em todas as aplicações e sem condição que a estreite.",
            // Política para um usuário, um papel ou uma aplicação não sustenta conclusão sobre o ambiente. Alcance
            // dependente de grupo não coletado → sinal ausente com o motivo → não avaliado.
            f =>
            {
                var defaults = f.Get(KnightSignalKey.SecurityDefaultsEnabled);
                var baseline = f.Get(KnightSignalKey.BaselineMfaPolicies);
                if (defaults.IsCollected && defaults.Flag == true)
                    return Passed("Baseline presente: security defaults habilitados.");
                if (baseline.IsCollected && (baseline.Count ?? 0) > 0)
                    return Passed($"Baseline presente: {baseline.Count} política(s) habilitada(s) exigem MFA com alvo declarado em todos os usuários e todas as aplicações. "
                        + "Isso não comprova a cobertura de cada conta: exclusões aparecem na evidência e o alcance para administradores é avaliado em AK-ENTRA-008.");
                if (!baseline.IsCollected)
                    return NotEvaluated(baseline.MissingReason ?? "políticas de acesso condicional não coletadas");
                if (!defaults.IsCollected)
                    return NotEvaluated("nenhuma política habilitada exige MFA de todos os usuários em todas as aplicações, mas os security defaults não puderam ser verificados — "
                        + (defaults.MissingReason ?? "sinal não coletado"));
                return Exposed("Sem security defaults e sem política habilitada exigindo MFA de todos os usuários em todas as aplicações — baseline ausente. "
                    + "Políticas de alcance restrito (usuários, papéis ou aplicações específicos, ou com condições) não sustentam uma base para o ambiente.", 0);
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

        // ==== Específicos do Google Workspace (coleta real Admin SDK + Reports) ====

        new KnightIndicatorDefinition(
            "AK-GWS-001", "1", "Superadministradores sem verificação em duas etapas (2SV)",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Critical, GoogleOnly,
            new[] { "PR.AA-01", "PR.AA-03" }, new[] { "T1078 · Valid Accounts", "T1078.004 · Cloud Accounts" },
            "Exigir 2SV (idealmente chaves de segurança) em todos os superadministradores e reduzir o número de superadmins ao mínimo.",
            "Número de superadministradores sem 2SV inscrito.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.SuperAdminsWithout2Sv, out var without, out var ne)) return ne;
                var total = f.Get(KnightSignalKey.SuperAdminsTotal);
                var totalTxt = total.IsCollected ? Fmt(total.Count) : "?";
                return without > 0
                    ? Exposed($"{without} de {totalTxt} superadministrador(es) sem 2SV inscrito.", (int)without)
                    : Passed($"Todos os {totalTxt} superadministrador(es) com 2SV inscrito.");
            }),

        new KnightIndicatorDefinition(
            "AK-GWS-002", "1", "Baixa cobertura de 2SV entre os usuários do diretório",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.High, GoogleOnly,
            new[] { "PR.AA-01", "PR.AA-02" }, Array.Empty<string>(),
            "Impor 2SV a todos os usuários ativos e monitorar a cobertura; preferir métodos resistentes a phishing.",
            $"Percentual de usuários ativos com 2SV inscrito comparado ao mínimo de {MinTwoStepVerificationCoveragePercent}%.",
            f =>
            {
                if (!TryRatio(f, KnightSignalKey.TwoStepVerificationCoveragePercent, out var pct, out var ne)) return ne;
                return pct < MinTwoStepVerificationCoveragePercent
                    ? Exposed($"Cobertura de 2SV em {pct.ToString("0.#", CultureInfo.InvariantCulture)}% (mínimo {MinTwoStepVerificationCoveragePercent}%).", 0)
                    : Passed($"Cobertura de 2SV em {pct.ToString("0.#", CultureInfo.InvariantCulture)}%.");
            }),

        new KnightIndicatorDefinition(
            "AK-GWS-003", "1", "Superadministradores sem atividade recente",
            KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Medium, GoogleOnly,
            new[] { "PR.AA-01", "GV.RR-02" }, new[] { "T1078 · Valid Accounts" },
            "Revisar e remover/reduzir superadministradores sem uso recente; adotar acesso privilegiado just-in-time.",
            $"Número de superadministradores sem login nos últimos {StalePrivilegedWindowDays} dias.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.StaleSuperAdmins, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} superadministrador(es) sem atividade nos últimos {StalePrivilegedWindowDays} dias.", (int)n)
                    : Passed("Nenhum superadministrador obsoleto.");
            }),

        new KnightIndicatorDefinition(
            "AK-GWS-004", "1", "Membros externos em grupos do Workspace",
            KnightIndicatorCategory.GuestAccess, SeverityLevel.Medium, GoogleOnly,
            new[] { "PR.AA-05", "GV.SC-04" }, new[] { "T1078.004 · Cloud Accounts" },
            "Revisar membros externos em grupos; restringir a associação externa e aprovar/expirar o acesso de terceiros.",
            "Número de membros externos (fora dos domínios da organização) em grupos do Workspace.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.ExternalGroupMembers, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} membro(s) externo(s) em grupos do Workspace.", (int)n)
                    : Passed("Nenhum membro externo em grupos do Workspace.");
            }),

        new KnightIndicatorDefinition(
            "AK-GWS-005", "1", "Compartilhamento externo recente de arquivos no Drive",
            KnightIndicatorCategory.GuestAccess, SeverityLevel.High, GoogleOnly,
            new[] { "GV.SC-04", "PR.AA-05" }, new[] { "T1567 · Exfiltration Over Web Service" },
            "Restringir o compartilhamento externo no Drive por política e revisar os eventos de exposição a links públicos/externos.",
            $"Número de eventos de mudança de visibilidade para acesso externo/público no Drive nos últimos {GoogleAuditWindowDays} dias.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.ExternalDriveSharingEvents, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} evento(s) de compartilhamento externo/público no Drive nos últimos {GoogleAuditWindowDays} dias.", (int)n)
                    : Passed($"Nenhum evento de compartilhamento externo no Drive nos últimos {GoogleAuditWindowDays} dias.");
            }),

        new KnightIndicatorDefinition(
            "AK-GWS-006", "1", "Autorizações OAuth de terceiros recentes",
            KnightIndicatorCategory.IdentityGovernance, SeverityLevel.Medium, GoogleOnly,
            new[] { "GV.SC-04", "PR.AA-05" }, new[] { "T1550.001 · Application Access Token" },
            "Revisar aplicativos OAuth de terceiros autorizados; restringir o consentimento do usuário e apps não confiáveis.",
            $"Número de clientes OAuth distintos autorizados nos últimos {GoogleAuditWindowDays} dias.",
            f =>
            {
                if (!TryCount(f, KnightSignalKey.RecentOAuthGrants, out var n, out var ne)) return ne;
                return n > 0
                    ? Exposed($"{n} cliente(s) OAuth distinto(s) autorizado(s) nos últimos {GoogleAuditWindowDays} dias — revisar necessidade e escopos.", (int)n)
                    : Passed($"Nenhuma autorização OAuth de terceiros nos últimos {GoogleAuditWindowDays} dias.");
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
    /// <summary>Avaliação só com fatos agregados: os controles de configuração ficam não avaliados, com o motivo.</summary>
    public static IReadOnlyList<KnightEvaluatedIndicator> Evaluate(KnightFactSet facts, KnightSourceType source) =>
        Evaluate(KnightEvaluationContext.FromFacts(facts), source);

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-01] Avaliação sobre o contexto COMPLETO da coleta relida do ADM: fatos, capacidades e
    /// configuração. É o mesmo motor — o contexto só amplia o que a regra de configuração enxerga.
    /// </summary>
    public static IReadOnlyList<KnightEvaluatedIndicator> Evaluate(KnightEvaluationContext context, KnightSourceType source)
    {
        var applicable = KnightCatalog.ForSource(source);
        var results = new List<KnightEvaluatedIndicator>(applicable.Count);
        foreach (var def in applicable)
        {
            try
            {
                if (def.Evaluate is { } evaluate)
                {
                    var o = evaluate(context);
                    results.Add(new KnightEvaluatedIndicator(
                        def, o.Status, o.Evidence, o.AffectedObjectCount, o.NotEvaluatedReason,
                        o.Affected.Concat(o.EvidenceObjects).ToList(), o.AffectedComplete, o.Limitation));
                    continue;
                }

                var outcome = def.Rule(context.Facts);
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
