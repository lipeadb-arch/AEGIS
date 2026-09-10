using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-ADM-02] Leitura do histórico mensal do ADM de identidade, sobre o <c>AegisScoreDbContext</c>.
///
/// SOMENTE LEITURA, e isso é uma decisão, não uma consequência: uma rota de consulta que consolidasse (ou pior,
/// expurgasse) faria uma operação destrutiva depender de alguém abrir uma tela. A consolidação é da manutenção
/// em fundo; aqui só se lê o que ela gravou.
///
/// A série é preenchida MÊS A MÊS no período pedido, inclusive nos meses sem linha — que são declarados "não
/// consolidado", nunca "sem dados". Confundir os dois transformaria uma manutenção que ainda não rodou em
/// afirmação sobre o ambiente do cliente.
/// </summary>
public sealed class IdentityHistoryQuery : IIdentityHistoryQuery
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public IdentityHistoryQuery(AegisScoreDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<IdentityHistoryViewDto> GetAsync(
        IdentityHistoryRangeRequest request, CancellationToken ct = default)
    {
        request ??= new IdentityHistoryRangeRequest();

        var now = _clock.GetUtcNow();
        var mesCorrente = IdentityAdmRetentionPolicy.MonthOf(now);
        var maisAntigo = IdentityAdmRetentionPolicy.OldestRetainedMonth(now);

        // O período é RECORTADO à janela retida, nunca expandido: pedir 2019 não pode devolver meses vazios
        // que pareceriam "sem coleta em 2019" — eles simplesmente não existem mais, e por prazo.
        var de = Maior(request.FromMonth ?? maisAntigo, maisAntigo);
        var ate = Menor(request.ToMonth ?? mesCorrente, mesCorrente);
        if (ate < de) ate = de;

        var vazio = new IdentityHistoryViewDto(
            de, ate, mesCorrente, maisAntigo,
            IdentityAdmRetentionPolicy.RetainedMonths, IdentityAdmRetentionPolicy.DetailRetentionDays,
            Array.Empty<IdentityHistoryDirectoryDto>());

        // Fail-closed: sem tenant ambiente nada é projetado (o Global Query Filter já zeraria a leitura, e
        // devolver a moldura vazia é a resposta honesta em vez de uma exceção genérica).
        if (_tenant.TenantId is null) return vazio;

        var consolidacoes = await _db.IdentityMonthlyRollups.AsNoTracking()
            .Include(r => r.Sets)
            .Where(r => r.Month >= de && r.Month <= ate)
            .ToListAsync(ct);

        if (consolidacoes.Count == 0) return vazio;

        var detalhes = await DetalheDasFotografiasAsync(consolidacoes, ct);

        var meses = MesesEntre(de, ate);

        var origens = consolidacoes
            .GroupBy(r => (r.Provider, r.DirectoryNamespace))
            .OrderBy(g => (int)g.Key.Provider)
            .ThenBy(g => g.Key.DirectoryNamespace, StringComparer.Ordinal)
            .Select(g => new IdentityHistoryDirectoryDto(
                g.Key.Provider.ToString(),
                g.Key.DirectoryNamespace,
                Serie(g.ToDictionary(r => r.Month), meses, mesCorrente, detalhes)))
            .ToList();

        return vazio with { Directories = origens };
    }

    /// <summary>
    /// O estado do detalhe de CADA fotografia exibida, numa consulta só e limitada às aquisições que a série
    /// realmente cita — no máximo uma por mês e por origem.
    ///
    /// É apurado na PRÓPRIA aquisição de referência porque o marcador de retenção do mês não responde a esta
    /// pergunta: ele registra que o expurgo passou pelo mês, o que é compatível tanto com uma fotografia
    /// íntegra (a coleta removida era outra, mais antiga) quanto com uma fotografia que perdeu só o detalhe
    /// sem que nada tenha saído por inteiro.
    ///
    /// A chave presente no dicionário significa "a linha existe"; o valor, "quando o detalhe dela expirou".
    /// A ausência da chave é a única coisa que este método afirma sobre o que sumiu — a CAUSA é decidida
    /// depois, e só quando há prova dela. Nada aqui escreve, consolida ou recompõe detalhe nenhum.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, DateTimeOffset?>> DetalheDasFotografiasAsync(
        IReadOnlyList<IdentityMonthlyRollup> consolidacoes, CancellationToken ct)
    {
        var ids = consolidacoes
            .Where(r => r.SnapshotAcquisitionId is not null)
            .Select(r => r.SnapshotAcquisitionId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return new Dictionary<Guid, DateTimeOffset?>();

        var linhas = await _db.IdentityAcquisitions.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.DetailRetiredAt })
            .ToListAsync(ct);

        return linhas.ToDictionary(l => l.Id, l => l.DetailRetiredAt);
    }

    /// <summary>
    /// A série de UMA origem, com a comparabilidade resolvida contra o mês ANTERIOR da própria série (e não
    /// contra "o mês passado do calendário"): é a comparação que a leitura vai fazer de fato.
    /// </summary>
    private static IReadOnlyList<IdentityHistoryMonthDto> Serie(
        IReadOnlyDictionary<DateOnly, IdentityMonthlyRollup> porMes,
        IReadOnlyList<DateOnly> meses,
        DateOnly mesCorrente,
        IReadOnlyDictionary<Guid, DateTimeOffset?> detalhes)
    {
        var serie = new List<IdentityHistoryMonthDto>(meses.Count);
        IdentityMonthlyRollup? anterior = null;

        foreach (var mes in meses)
        {
            porMes.TryGetValue(mes, out var r);
            serie.Add(Mes(mes, r, anterior, mesCorrente, detalhes));

            // O elo de comparação é o último mês COM DADOS. Um mês sem coleta no meio não deve fazer a
            // comparabilidade se perder para sempre — ele apenas não é o termo de comparação.
            if (r is not null && r.HasData) anterior = r;
        }

        return serie;
    }

    private static IdentityHistoryMonthDto Mes(
        DateOnly mes,
        IdentityMonthlyRollup? r,
        IdentityMonthlyRollup? anterior,
        DateOnly mesCorrente,
        IReadOnlyDictionary<Guid, DateTimeOffset?> detalhes)
    {
        var rotulo = mes.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var provisorio = mes == mesCorrente;

        if (r is null)
        {
            // Sem linha ≠ sem coleta. A manutenção pode simplesmente ainda não ter passado por aqui, e dizer
            // "sem dados" nesse caso seria uma afirmação sobre o ambiente do cliente que ninguém apurou.
            return new IdentityHistoryMonthDto(
                rotulo, IdentityHistoryMonthState.NotConsolidated, provisorio, 0, 0, 0,
                IdentityHistoryDetailAvailability.NoSnapshot, null, null, null, null,
                Array.Empty<IdentityHistorySetDto>(), false,
                "Mês ainda não consolidado — a ausência de valores aqui não afirma ausência de coleta.",
                null, null);
        }

        var estado = r.HasData
            ? IdentityHistoryMonthState.Collected
            : r.AcquisitionCount == 0
                ? IdentityHistoryMonthState.NoCollection
                : IdentityHistoryMonthState.NoData;

        var (comparavel, ressalva) = Comparabilidade(r, anterior, estado);

        var snapshot = r.HasData
            ? new IdentityHistoryProvenanceDto(
                r.SnapshotAcquisitionId!.Value,
                r.SnapshotAcquiredAt!.Value,
                r.SnapshotObservedAt,
                r.SnapshotConnectorConfigId,
                r.SnapshotSourceLabel,
                r.SnapshotSchemaVersion,
                r.SnapshotNormalizationVersion,
                (r.SnapshotState ?? KnightSourceState.NotConfigured).ToString())
            : null;

        var tentativa = r.LastAttemptAcquisitionId is { } tentativaId
            ? new IdentityHistoryAttemptDto(
                tentativaId,
                r.LastAttemptAt ?? r.ConsolidatedAt,
                (r.LastAttemptState ?? KnightSourceState.NotConfigured).ToString(),
                r.LastAttemptDetail)
            : null;

        var conjuntos = r.Sets
            .OrderBy(s => (int)s.Set)
            .Select(s =>
            {
                var descricao = IdentityObservationSetCatalog.Describe(s.Set);
                return new IdentityHistorySetDto(
                    s.Set.ToString(),
                    descricao.Label,
                    s.Outcome.ToString(),
                    // Um número num conjunto NÃO coletado seria lido como apuração. Nulo é a resposta honesta.
                    s.Outcome == IdentityObservationSetOutcome.NotAttempted ? null : s.ObservedCount,
                    s.PreservedCount,
                    s.IsComplete,
                    s.Limitation,
                    descricao.DoesNotProve);
            })
            .ToList();

        // DETALHE EXPIRADO ≠ conjunto vazio ≠ ausência na origem. Os valores acima são os APURADOS na coleta e
        // continuam sendo apresentados como tais em TODOS os casos; o que as notas declaram é o que aconteceu
        // com a evidência por trás deles. Sem essa distinção, um mês com detalhe expurgado seria
        // indistinguível de um mês em que a coleta olhou e não encontrou nada.
        var disponibilidade = Disponibilidade(r, detalhes);

        return new IdentityHistoryMonthDto(
            rotulo, estado, provisorio, r.AcquisitionCount, r.DataProducingCount,
            r.RetiredAcquisitionCount, disponibilidade, NotaDoMes(r), NotaDaFotografia(r, disponibilidade),
            snapshot, tentativa, conjuntos, comparavel, ressalva, r.ConsolidatedAt, r.ConsolidationVersion);
    }

    /// <summary>
    /// O que aconteceu com o detalhe da coleta que originou a FOTOGRAFIA exibida — apurado nela, e não
    /// deduzido do marcador do mês.
    ///
    /// A linha ausente é o caso delicado: sumir não diz por quê. Só o COMPROVANTE específico gravado pela
    /// retenção, com o identificador DESTA fotografia, prova que foi ela. A fronteira varrida do mês não serve:
    /// ela avança por outras coletas enquanto uma fotografia protegida é pulada, e uma cascata posterior que
    /// levasse essa fotografia seria atribuída ao expurgo só pela comparação das datas. Sem o comprovante, a
    /// ausência fica declarada como de causa desconhecida.
    /// </summary>
    private static IdentityHistoryDetailAvailability Disponibilidade(
        IdentityMonthlyRollup r, IReadOnlyDictionary<Guid, DateTimeOffset?> detalhes)
    {
        if (r.SnapshotAcquisitionId is not { } fotografia)
            return IdentityHistoryDetailAvailability.NoSnapshot;

        if (detalhes.TryGetValue(fotografia, out var expiradoEm))
            return expiradoEm is null
                ? IdentityHistoryDetailAvailability.Available
                : IdentityHistoryDetailAvailability.DetailRetired;

        return r.RetentionRemovedSnapshotAcquisitionId == fotografia
            ? IdentityHistoryDetailAvailability.RemovedByRetention
            : IdentityHistoryDetailAvailability.Unavailable;
    }

    /// <summary>
    /// Atividade de RETENÇÃO no mês — quantas coletas o expurgo levou, e até onde ele varreu. É afirmação
    /// sobre o mês e nada além dele: um mês com coletas removidas pode continuar exibindo uma fotografia
    /// íntegra, e um mês sem nenhuma remoção pode exibir uma fotografia que perdeu só o detalhe.
    /// </summary>
    private static string? NotaDoMes(IdentityMonthlyRollup r) =>
        r.RetiredAcquisitionCount <= 0
            ? null
            : $"Retenção já aplicada neste mês: {r.RetiredAcquisitionCount} de {r.AcquisitionCount} "
              + "coleta(s) registrada(s) removida(s) por vencimento"
              + (r.RetentionSweptThroughAt is { } ate
                  ? $" (varrido até {ate.ToUniversalTime():yyyy-MM-dd})"
                  : "")
              + ". É atividade de retenção NO MÊS, e não afirmação sobre a fotografia exibida.";

    /// <summary>O que dizer sobre a fotografia quando o detalhe dela não está mais disponível.</summary>
    private static string? NotaDaFotografia(
        IdentityMonthlyRollup r, IdentityHistoryDetailAvailability disponibilidade) =>
        disponibilidade switch
        {
            IdentityHistoryDetailAvailability.DetailRetired =>
                "Detalhe operacional da coleta que originou esta fotografia EXPIRADO POR RETENÇÃO: os números "
                + "e a completude continuam sendo os apurados por ela, mas a lista de objetos observados não "
                + "está mais disponível. A coleta em si permanece registrada.",

            IdentityHistoryDetailAvailability.RemovedByRetention =>
                "A coleta que originou esta fotografia foi REMOVIDA POR RETENÇÃO"
                + (r.RetentionRemovedSnapshotAt is { } removidaEm
                    ? $" em {removidaEm.ToUniversalTime():yyyy-MM-dd}"
                    : "")
                + ": os números e a completude preservados aqui são os que ela apurou, e a lista de objetos "
                + "observados não está mais disponível.",

            IdentityHistoryDetailAvailability.Unavailable =>
                "A coleta que originou esta fotografia não está mais registrada, e não há comprovante de que a "
                + "retenção a tenha removido — a origem pode ter sido excluída, levando a evidência junto. "
                + "Os números e a completude preservados aqui são os que ela apurou; a causa da ausência não "
                + "é conhecida e não é atribuída ao expurgo.",

            _ => null,
        };

    /// <summary>
    /// Quando comparar dois meses induziria a erro. Uma mudança de versão de schema ou de normalização é uma
    /// mudança em COMO se lê o diretório: o número pode subir ou descer sem que nada tenha mudado no ambiente
    /// do cliente, e apresentar isso como evolução de postura seria uma conclusão inventada.
    /// </summary>
    private static (bool Comparavel, string? Ressalva) Comparabilidade(
        IdentityMonthlyRollup atual, IdentityMonthlyRollup? anterior, IdentityHistoryMonthState estado)
    {
        if (estado != IdentityHistoryMonthState.Collected)
            return (false, estado == IdentityHistoryMonthState.NoCollection
                ? "Sem coleta neste mês — não há valores para comparar."
                : "Nenhuma coleta deste mês produziu dados — não há valores para comparar.");

        if (anterior is null)
            return (false, "Primeiro mês com dados da série — não há mês anterior com que comparar.");

        var schemaMudou = !string.Equals(
            atual.SnapshotSchemaVersion, anterior.SnapshotSchemaVersion, StringComparison.Ordinal);
        var normalizacaoMudou = !string.Equals(
            atual.SnapshotNormalizationVersion, anterior.SnapshotNormalizationVersion, StringComparison.Ordinal);

        if (schemaMudou || normalizacaoMudou)
        {
            var oQue = schemaMudou && normalizacaoMudou
                ? "o contrato do ADM e a normalização mudaram"
                : schemaMudou ? "o contrato do ADM mudou" : "a normalização mudou";

            return (false,
                $"Comparação com o mês anterior tem ressalva: {oQue} entre os dois "
                + $"({anterior.SnapshotSchemaVersion}/{anterior.SnapshotNormalizationVersion} → "
                + $"{atual.SnapshotSchemaVersion}/{atual.SnapshotNormalizationVersion}). Uma diferença de "
                + "número pode vir da interpretação, e não do ambiente.");
        }

        // Parcialidade não impede a comparação, mas precisa aparecer: um mês parcial é um PISO, e um piso menor
        // que o total do mês anterior não é melhora.
        if (atual.SnapshotState == KnightSourceState.PartialCollection
            || anterior.SnapshotState == KnightSourceState.PartialCollection)
        {
            return (true,
                "Um dos meses comparados vem de coleta declaradamente PARCIAL: os valores são um piso, "
                + "não o total observado.");
        }

        return (true, null);
    }

    private static IReadOnlyList<DateOnly> MesesEntre(DateOnly de, DateOnly ate)
    {
        var meses = new List<DateOnly>();
        for (var m = de; m <= ate; m = m.AddMonths(1)) meses.Add(m);
        return meses;
    }

    private static DateOnly Maior(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Menor(DateOnly a, DateOnly b) => a < b ? a : b;
}
