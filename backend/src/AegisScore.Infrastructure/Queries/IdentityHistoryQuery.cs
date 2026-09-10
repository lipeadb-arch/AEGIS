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

        var meses = MesesEntre(de, ate);

        var origens = consolidacoes
            .GroupBy(r => (r.Provider, r.DirectoryNamespace))
            .OrderBy(g => (int)g.Key.Provider)
            .ThenBy(g => g.Key.DirectoryNamespace, StringComparer.Ordinal)
            .Select(g => new IdentityHistoryDirectoryDto(
                g.Key.Provider.ToString(),
                g.Key.DirectoryNamespace,
                Serie(g.ToDictionary(r => r.Month), meses, mesCorrente)))
            .ToList();

        return vazio with { Directories = origens };
    }

    /// <summary>
    /// A série de UMA origem, com a comparabilidade resolvida contra o mês ANTERIOR da própria série (e não
    /// contra "o mês passado do calendário"): é a comparação que a leitura vai fazer de fato.
    /// </summary>
    private static IReadOnlyList<IdentityHistoryMonthDto> Serie(
        IReadOnlyDictionary<DateOnly, IdentityMonthlyRollup> porMes,
        IReadOnlyList<DateOnly> meses,
        DateOnly mesCorrente)
    {
        var serie = new List<IdentityHistoryMonthDto>(meses.Count);
        IdentityMonthlyRollup? anterior = null;

        foreach (var mes in meses)
        {
            porMes.TryGetValue(mes, out var r);
            serie.Add(Mes(mes, r, anterior, mesCorrente));

            // O elo de comparação é o último mês COM DADOS. Um mês sem coleta no meio não deve fazer a
            // comparabilidade se perder para sempre — ele apenas não é o termo de comparação.
            if (r is not null && r.HasData) anterior = r;
        }

        return serie;
    }

    private static IdentityHistoryMonthDto Mes(
        DateOnly mes, IdentityMonthlyRollup? r, IdentityMonthlyRollup? anterior, DateOnly mesCorrente)
    {
        var rotulo = mes.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var provisorio = mes == mesCorrente;

        if (r is null)
        {
            // Sem linha ≠ sem coleta. A manutenção pode simplesmente ainda não ter passado por aqui, e dizer
            // "sem dados" nesse caso seria uma afirmação sobre o ambiente do cliente que ninguém apurou.
            return new IdentityHistoryMonthDto(
                rotulo, IdentityHistoryMonthState.NotConsolidated, provisorio, 0, 0, 0, null, null, null,
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
        // continuam sendo apresentados como tais; o que a nota declara é que a lista de objetos por trás deles
        // já não existe. Sem essa distinção, um mês com detalhe expurgado seria indistinguível de um mês em que
        // a coleta olhou e não encontrou nada.
        var notaRetencao = r.RetentionSweptThroughAt is { } varridoAte
            ? $"Detalhe operacional deste mês EXPIRADO POR RETENÇÃO (varrido até "
              + $"{varridoAte.ToUniversalTime():yyyy-MM-dd}): os números e a completude são os apurados na "
              + "coleta, mas a lista de objetos observados não está mais disponível."
            : null;

        return new IdentityHistoryMonthDto(
            rotulo, estado, provisorio, r.AcquisitionCount, r.DataProducingCount,
            r.RetiredAcquisitionCount, notaRetencao, snapshot, tentativa,
            conjuntos, comparavel, ressalva, r.ConsolidatedAt, r.ConsolidationVersion);
    }

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
