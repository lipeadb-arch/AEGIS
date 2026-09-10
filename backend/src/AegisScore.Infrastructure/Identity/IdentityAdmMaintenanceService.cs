using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Identity.Adm;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Identity;

/// <summary>
/// [AEGIS-ADM-02] Manutenção do ADM de identidade: CONSOLIDA os meses e aplica a RETENÇÃO — as duas na MESMA
/// transação e sob a MESMA trava de diretório.
///
/// Por que juntas, e não em sequência. A consolidação lê as aquisições do mês para escolher a fotografia; a
/// retenção remove aquisições vencidas. Fazê-las em transações separadas deixaria uma janela entre o commit da
/// primeira e a abertura da segunda — e é exatamente nessa janela que uma coleta ATRASADA daquele mês entra,
/// não participa da consolidação que acabou de fechar, e é removida pela retenção que acabou de começar. A
/// evidência sairia sem nunca ter entrado no histórico, e não haveria de onde recalculá-la. Uma única
/// transação com uma única trava não tem essa janela; e, de quebra, uma falha em qualquer ponto desfaz tudo,
/// o que torna "falha na consolidação impede a remoção" uma propriedade do BANCO em vez de uma promessa do
/// código.
///
/// Além da atomicidade, a retenção VERIFICA a pré-condição por candidata: o mês daquela aquisição tem de ter
/// sido consolidado NESTA passada (relido do banco, com <c>ConsolidatedAt</c> igual ao instante de referência
/// e a versão corrente da regra). Contar doze linhas mensais não prova nada — elas podem ser de ontem, de
/// outra versão da regra, ou de uma passada que não viu esta coleta. Um mês que não passe nessa conferência
/// PRESERVA as suas candidatas e diz por quê.
///
/// Três caminhos disputam as mesmas linhas — coleta, consolidação e retenção. Todos passam pela MESMA trava
/// consultiva de diretório (<see cref="IdentityDirectoryLock"/>), e por isso se serializam em vez de decidir
/// cada um sobre uma foto vencida do estado. Dentro dela, os candidatos à remoção são travados linha a linha e
/// as referências são RECONFERIDAS: uma consulta de elegibilidade feita antes da transação não vale nada,
/// porque a avaliação que passaria a citar a aquisição pode acontecer no intervalo.
///
/// Opera SEM tenant ambiente (como os workers): descobre as origens com <c>IgnoreQueryFilters</c> num contexto
/// de sistema e depois trabalha tenant a tenant, sob <see cref="SystemTenantContext"/> — o filtro restringe a
/// leitura e o stamping fail-closed carimba a escrita.
/// </summary>
public sealed class IdentityAdmMaintenanceService : IIdentityAdmMaintenanceService
{
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<IdentityAdmMaintenanceService>? _log;

    public IdentityAdmMaintenanceService(
        DbContextOptions<AegisScoreDbContext> options,
        TimeProvider clock,
        ILogger<IdentityAdmMaintenanceService>? log = null)
    {
        _options = options;
        _clock = clock;
        _log = log;
    }

    public async Task<IdentityAdmMaintenanceReport> RunAsync(
        IdentityAdmMaintenanceRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (request.MaxDirectories <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "O lote de origens precisa ser positivo.");
        if (request.MaxAcquisitionsPerDirectory <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(request), "O teto de aquisições por origem precisa ser positivo.");

        // PRÉ-CONDIÇÃO da retenção, recusada aqui em vez de descoberta lá dentro. Uma passada que remove sem
        // consolidar apaga a matéria-prima de um histórico que ninguém gravou — e o mês perdido não volta,
        // porque não há de onde recalculá-lo. Vale também para a SIMULAÇÃO: prever um expurgo sob condições
        // que a execução real recusaria produziria uma prévia que não corresponde a nada.
        if (request.Remove && !request.Consolidate)
            throw new ArgumentException(
                "Remoção pedida sem consolidação: a retenção do ADM só é segura depois de o mês da aquisição "
                + "ter sido consolidado e confirmado na MESMA transação. Peça Consolidate=true junto de "
                + "Remove=true.",
                nameof(request));

        var now = (request.Now ?? _clock.GetUtcNow()).ToUniversalTime();
        var window = IdentityAdmRetentionPolicy.RetainedWindow(now);
        var detailCutoff = IdentityAdmRetentionPolicy.DetailCutoff(now);
        var oldestMonth = IdentityAdmRetentionPolicy.OldestRetainedMonth(now);

        var relatorios = new List<IdentityAdmDirectoryReport>();
        var cursor = request.ResumeAfter;
        var completo = true;

        while (relatorios.Count < request.MaxDirectories)
        {
            // Parada GRACIOSA entre origens: cancelar no meio de uma passada não pode perder o que já foi
            // feito nem a posição. O que ficou de fora é retomado pelo cursor, sem reprocessar o resto.
            if (ct.IsCancellationRequested) { completo = false; break; }

            // Paginação NO BANCO, uma origem por vez a partir do cursor. Materializar a lista completa de
            // origens antes de aplicar o teto tornaria o "lote" um limite só da escrita: a leitura já teria
            // percorrido todos os tenants, que é justamente o que a manutenção não pode fazer numa operação.
            var proxima = await NextDirectoryAsync(cursor, ct);
            if (proxima is null) break;

            var dir = proxima.Directory;

            IdentityAdmDirectoryReport relatorio;
            bool esgotouLote;
            try
            {
                (relatorio, esgotouLote) = await ProcessDirectoryAsync(
                    dir, now, window, detailCutoff, oldestMonth, request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                completo = false;
                break;
            }
            catch (Exception ex)
            {
                // Falha ISOLADA por origem. Nada foi escrito para ela (consolidação e retenção correm na mesma
                // transação, e o rollback é do conjunto), e o cursor AVANÇA: uma origem com problema crônico
                // não pode impedir para sempre que as seguintes sejam visitadas. Ela volta a ser tentada na
                // próxima varredura completa.
                _log?.LogError(ex,
                    "Manutenção do ADM falhou em {Namespace} (tenant {TenantId}); as demais origens seguem.",
                    dir.DirectoryNamespace, dir.TenantId);

                relatorios.Add(new IdentityAdmDirectoryReport(
                    dir, 0, 0, 0, 0, 0, 0, 0, Array.Empty<IdentityAdmProtectedAcquisition>(), ex.Message));
                cursor = proxima;
                continue;
            }

            relatorios.Add(relatorio);

            if (esgotouLote)
            {
                // A origem tinha mais candidatos do que o teto permite examinar. O cursor NÃO avança para ela:
                // a próxima passada precisa voltar a esta origem, e não pulá-la para sempre.
                completo = false;
                break;
            }

            cursor = proxima;
        }

        // Distinguir "acabaram as origens" de "acabou o lote": só o segundo devolve cursor. Sem essa
        // distinção o worker não teria como saber se já varreu tudo — e recomeçaria do zero para sempre.
        if (completo && relatorios.Count >= request.MaxDirectories)
            completo = await NextDirectoryAsync(cursor, ct) is null;

        return new IdentityAdmMaintenanceReport(
            now, detailCutoff, oldestMonth, request.Simulate, request.Remove, completo,
            completo ? null : cursor, relatorios);
    }

    // ---- Descoberta das origens ----------------------------------------------------------------------

    /// <summary>
    /// A PRÓXIMA origem depois do cursor, escolhida pelo BANCO — em ordem determinística
    /// <c>(tenant, provedor, namespace)</c>, que é o que transforma "continue de onde parou" numa posição em
    /// vez de uma esperança.
    ///
    /// Duas FASES, percorridas em sequência: primeiro as origens que ainda têm aquisições; depois as ÓRFÃS,
    /// que só existem como consolidação porque todas as aquisições delas já saíram. Sem a segunda fase, um
    /// diretório esvaziado sumiria da varredura e as consolidações vencidas dele nunca expirariam — um
    /// crescimento silencioso exatamente onde a retenção deveria agir.
    ///
    /// ⚠️ Fases em SEQUÊNCIA, e não uma união das duas populações. O filtro do cursor e a ordenação precisam
    /// ser resolvidos pelo mesmo banco, na mesma consulta: comparar as chaves de duas consultas em memória
    /// exigiria que a ordem do .NET coincidisse com a do banco, e não coincide — <c>Guid.CompareTo</c> compara
    /// o primeiro campo COM SINAL, o PostgreSQL compara <c>uuid</c> byte a byte sem sinal, e o SQLite compara
    /// o BLOB na ordem interna do .NET. No primeiro desacordo, uma origem seria pulada para sempre.
    /// </summary>
    private async Task<IdentityAdmCursor?> NextDirectoryAsync(IdentityAdmCursor? after, CancellationToken ct)
    {
        await using var probe = new AegisScoreDbContext(_options, new SystemTenantContext(null));

        if (after is null || !after.OrphanRollups)
        {
            var aquisicoes = probe.IdentityAcquisitions.IgnoreQueryFilters();

            if (after?.Directory is { } posicao)
            {
                var t = posicao.TenantId;
                var p = posicao.Provider;
                var n = posicao.DirectoryNamespace;

                aquisicoes = aquisicoes.Where(a =>
                    a.TenantId > t
                    || (a.TenantId == t
                        && (a.Provider > p
                            || (a.Provider == p && a.DirectoryNamespace.CompareTo(n) > 0))));
            }

            var comAquisicoes = await aquisicoes
                .OrderBy(a => a.TenantId).ThenBy(a => a.Provider).ThenBy(a => a.DirectoryNamespace)
                .Select(a => new OrigemLinha(a.TenantId, a.Provider, a.DirectoryNamespace))
                .FirstOrDefaultAsync(ct);

            if (comAquisicoes is not null) return Cursor(comAquisicoes, orfa: false);

            // Primeira fase esgotada: a segunda começa do INÍCIO da própria população.
            after = null;
        }

        var orfas = probe.IdentityMonthlyRollups.IgnoreQueryFilters()
            .Where(r => !probe.IdentityAcquisitions.IgnoreQueryFilters().Any(
                a => a.TenantId == r.TenantId && a.Provider == r.Provider
                     && a.DirectoryNamespace == r.DirectoryNamespace));

        if (after?.Directory is { } deixada)
        {
            var t = deixada.TenantId;
            var p = deixada.Provider;
            var n = deixada.DirectoryNamespace;

            orfas = orfas.Where(r =>
                r.TenantId > t
                || (r.TenantId == t
                    && (r.Provider > p
                        || (r.Provider == p && r.DirectoryNamespace.CompareTo(n) > 0))));
        }

        var soConsolidacao = await orfas
            .OrderBy(r => r.TenantId).ThenBy(r => r.Provider).ThenBy(r => r.DirectoryNamespace)
            .Select(r => new OrigemLinha(r.TenantId, r.Provider, r.DirectoryNamespace))
            .FirstOrDefaultAsync(ct);

        return soConsolidacao is null ? null : Cursor(soConsolidacao, orfa: true);
    }

    private static IdentityAdmCursor Cursor(OrigemLinha linha, bool orfa) =>
        new(new IdentityAdmDirectoryKey(linha.TenantId, linha.Provider, linha.DirectoryNamespace), orfa);

    // ---- Uma origem ----------------------------------------------------------------------------------

    private async Task<(IdentityAdmDirectoryReport Report, bool BatchExhausted)> ProcessDirectoryAsync(
        IdentityAdmDirectoryKey dir,
        DateTimeOffset now,
        IReadOnlyList<DateOnly> window,
        DateTimeOffset detailCutoff,
        DateOnly oldestMonth,
        IdentityAdmMaintenanceRequest request,
        CancellationToken ct)
    {
        await using var db = new AegisScoreDbContext(_options, new SystemTenantContext(dir.TenantId));

        if (request.Simulate)
            return await SimulateAsync(db, dir, window, detailCutoff, oldestMonth, request, ct);

        // UMA transação para consolidar E remover, sob UMA trava de diretório. Ver o cabeçalho da classe: é a
        // ausência de janela entre as duas etapas que impede uma coleta atrasada de ser removida sem nunca ter
        // sido consolidada.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockDirectoryAsync(db, dir, ct);

        var (rollups, mesesEscritos) = await ConsolidarAsync(db, dir, now, window, ct);

        // CONFIRMAÇÃO relendo do BANCO, dentro da transação. Não é a contagem de doze linhas: é a lista dos
        // meses cuja consolidação está gravada COM O INSTANTE DESTA PASSADA e com a versão corrente da regra.
        // É essa lista que autoriza a remoção de cada candidata, mês a mês.
        var confirmados = await ConfirmarConsolidacaoAsync(db, dir, window, now, ct);

        if (confirmados.Count < window.Count)
            throw new InvalidOperationException(
                $"A consolidação mensal de '{dir.DirectoryNamespace}' não foi confirmada no banco "
                + $"({confirmados.Count} de {window.Count} meses relidos com ConsolidatedAt desta passada). "
                + "Nada será removido nesta origem: remover a evidência que sustenta um histórico não "
                + "confirmado o destruiria sem possibilidade de recálculo.");

        if (!request.Remove)
        {
            await tx.CommitAsync(ct);
            return (new IdentityAdmDirectoryReport(
                dir, mesesEscritos, 0, 0, 0, 0, 0,
                await ProtegidasRetidasAsync(db, dir, detailCutoff, ct),
                Array.Empty<IdentityAdmProtectedAcquisition>()), false);
        }

        var retencao = await RetainAsync(
            db, dir, now, detailCutoff, oldestMonth, request, rollups, confirmados, ct);

        await tx.CommitAsync(ct);

        _log?.LogInformation(
            "Retenção do ADM em {Namespace}: {Removidas} aquisição(ões) removida(s), {Expirado} com detalhe "
            + "expirado, {Protegidas} preservada(s) com motivo, {Consolidacoes} consolidação(ões) fora da janela.",
            dir.DirectoryNamespace, retencao.Report.AcquisitionsRemoved, retencao.Report.DetailExpired,
            retencao.Report.Protected.Count, retencao.Report.RollupsRemoved);

        return (retencao.Report with { MonthsConsolidated = mesesEscritos }, retencao.BatchExhausted);
    }

    // ---- Consolidação mensal -------------------------------------------------------------------------

    /// <summary>
    /// Recalcula os 12 meses da janela. Recalcular TODOS (e não só o corrente) é o que absorve uma coleta
    /// ATRASADA sem depender da ordem de chegada — e, como o cálculo é determinístico, duas passadas em
    /// qualquer ordem convergem para o mesmo resultado.
    ///
    /// A regra de NÃO-REGRESSÃO não é uma comparação de contagens. Um mês consolidado com dez aquisições cujos
    /// originais foram removidos passaria a recalcular "uma", e uma guarda por contagem descartaria a coleta
    /// atrasada legítima só porque 1 &lt; 10 — perdendo justamente a evidência mais recente do mês. O que
    /// decide é a PROVENIÊNCIA: a fotografia gravada e a candidata recalculada competem pelo mesmo desempate
    /// determinístico do ADM-01 (maior instante; no empate exato, maior identificador). Quem estava lá só é
    /// substituído por quem é comprovadamente mais recente — nunca por ausência.
    /// </summary>
    private static async Task<(Dictionary<DateOnly, IdentityMonthlyRollup> Rollups, int Written)> ConsolidarAsync(
        AegisScoreDbContext db,
        IdentityAdmDirectoryKey dir,
        DateTimeOffset now,
        IReadOnlyList<DateOnly> window,
        CancellationToken ct)
    {
        var mesCorrente = IdentityAdmRetentionPolicy.MonthOf(now);
        var calculados = await CalcularMesesAsync(db, dir, window, ct);

        var primeiroMes = window[0];
        var ultimoMes = window[window.Count - 1];

        var existentes = await db.IdentityMonthlyRollups
            .Include(r => r.Sets)
            .Where(r => r.Provider == dir.Provider && r.DirectoryNamespace == dir.DirectoryNamespace
                        && r.Month >= primeiroMes && r.Month <= ultimoMes)
            .ToListAsync(ct);

        var porMes = existentes.ToDictionary(r => r.Month);
        var trocaramDeFotografia = new List<(IdentityMonthlyRollup Rollup, Guid SnapshotId)>();
        var escritos = 0;

        foreach (var mes in calculados)
        {
            porMes.TryGetValue(mes.Month, out var rollup);

            if (rollup is null)
            {
                rollup = new IdentityMonthlyRollup
                {
                    Provider = dir.Provider,
                    DirectoryNamespace = dir.DirectoryNamespace,
                    Month = mes.Month,
                };
                db.IdentityMonthlyRollups.Add(rollup);
                porMes[mes.Month] = rollup;
            }

            var trocou = AplicarFotografia(rollup, mes.Snapshot);
            AplicarUltimaTentativa(rollup, mes.LastAttempt);

            // CONTAGENS conservadas: sobreviventes + as que a retenção já removeu. Recontar apenas as linhas
            // presentes faria o passado encolher a cada expurgo; somar as novas sobre o total anterior
            // contaria a mesma aquisição outra vez em cada manutenção. A soma das duas parcelas não muda
            // quando uma aquisição migra de "presente" para "removida" — e é essa invariante que torna a
            // reconsolidação idempotente mesmo depois do expurgo.
            rollup.AcquisitionCount = mes.SurvivingCount + rollup.RetiredAcquisitionCount;
            rollup.DataProducingCount = mes.SurvivingDataProducingCount + rollup.RetiredDataProducingCount;

            rollup.IsProvisional = mes.Month == mesCorrente;
            rollup.ConsolidatedAt = now;
            rollup.ConsolidationVersion = IdentityAdmRetentionPolicy.ConsolidationVersion;

            if (trocou && rollup.SnapshotAcquisitionId is { } novaFoto)
                trocaramDeFotografia.Add((rollup, novaFoto));

            escritos++;
        }

        await db.SaveChangesAsync(ct);

        if (trocaramDeFotografia.Count > 0)
            await SubstituirConjuntosAsync(db, trocaramDeFotografia, ct);

        return (porMes, escritos);
    }

    /// <summary>
    /// Os valores por conjunto da nova fotografia. Só são reescritos nos meses cuja fotografia MUDOU: nos
    /// demais, os valores gravados podem ser a única cópia que restou (a aquisição de origem pode já ter
    /// saído), e recalculá-los a partir do que sobrou os apagaria.
    /// </summary>
    private static async Task SubstituirConjuntosAsync(
        AegisScoreDbContext db,
        List<(IdentityMonthlyRollup Rollup, Guid SnapshotId)> trocaram,
        CancellationToken ct)
    {
        var ids = trocaram.Select(x => x.SnapshotId).ToList();
        var estados = await db.IdentityObservationSetStates.AsNoTracking()
            .Where(s => ids.Contains(s.AcquisitionId))
            .ToListAsync(ct);

        var porAquisicao = estados
            .GroupBy(s => s.AcquisitionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => (int)s.Set).ToList());

        // Filhos: DESCARREGAR e só então inserir, em dois SaveChanges dentro da MESMA transação.
        // ⚠️ Nunca remover da coleção de navegação de um agregado com FK COMPOSTA (Id, TenantId): severar a
        // relação faz o EF marcar o filho como Modified com a FK zerada, e o guard de escrita multi-tenant
        // recusa a operação como "linha inexistente ou fora do tenant". Também não se pode inserir os novos na
        // mesma unidade em que os antigos saem: o índice único (tenant, consolidação, conjunto) rejeitaria a
        // sobreposição, porque a ordem entre DELETE e INSERT no lote não é garantida.
        var antigos = trocaram.SelectMany(x => x.Rollup.Sets).ToList();
        if (antigos.Count > 0)
        {
            db.IdentityMonthlyRollupSets.RemoveRange(antigos);
            await db.SaveChangesAsync(ct);
            foreach (var (rollup, _) in trocaram) rollup.Sets.Clear();
        }

        foreach (var (rollup, snapshotId) in trocaram)
        {
            if (!porAquisicao.TryGetValue(snapshotId, out var doSnapshot)) continue;

            foreach (var s in doSnapshot)
            {
                db.IdentityMonthlyRollupSets.Add(new IdentityMonthlyRollupSet
                {
                    RollupId = rollup.Id,
                    Set = s.Set,
                    Outcome = s.Outcome,
                    ObservedCount = s.ObservedCount,
                    PreservedCount = s.PreservedCount,
                    IsComplete = s.IsComplete,
                    Limitation = s.Limitation,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A fotografia do mês: vence a MAIS RECENTE entre a gravada e a candidata recalculada. Devolve
    /// <c>true</c> quando ela mudou — e só então os valores por conjunto são reescritos.
    /// </summary>
    private static bool AplicarFotografia(IdentityMonthlyRollup rollup, AquisicaoResumo? candidata)
    {
        if (candidata is null) return false;                             // ausência não substitui presença
        if (candidata.Id == rollup.SnapshotAcquisitionId) return false;  // a mesma de sempre

        if (rollup.SnapshotAcquisitionId is { } gravada
            && rollup.SnapshotAcquiredAt is { } gravadaEm
            && !MaisRecenteQue(candidata.AcquiredAt, candidata.Id, gravadaEm, gravada))
            return false;

        rollup.SnapshotAcquisitionId = candidata.Id;
        rollup.SnapshotAcquiredAt = candidata.AcquiredAt;
        rollup.SnapshotObservedAt = candidata.ObservedAt;
        rollup.SnapshotConnectorConfigId = candidata.ConnectorConfigId;
        rollup.SnapshotSourceLabel = candidata.SourceLabel;
        rollup.SnapshotSchemaVersion = candidata.SchemaVersion;
        rollup.SnapshotNormalizationVersion = candidata.NormalizationVersion;
        rollup.SnapshotState = candidata.State;
        return true;
    }

    /// <summary>
    /// A última TENTATIVA do mês — escolhida entre TODAS, inclusive as que falharam. Responde pergunta
    /// diferente da fotografia, e num mês em que a integração quebrou as duas divergem.
    /// </summary>
    private static void AplicarUltimaTentativa(IdentityMonthlyRollup rollup, AquisicaoResumo? candidata)
    {
        if (candidata is null) return;
        if (candidata.Id == rollup.LastAttemptAcquisitionId) return;

        if (rollup.LastAttemptAcquisitionId is { } gravada
            && rollup.LastAttemptAt is { } gravadaEm
            && !MaisRecenteQue(candidata.AcquiredAt, candidata.Id, gravadaEm, gravada))
            return;

        rollup.LastAttemptAcquisitionId = candidata.Id;
        rollup.LastAttemptAt = candidata.AcquiredAt;
        rollup.LastAttemptState = candidata.State;
        rollup.LastAttemptDetail = candidata.Detail;
    }

    /// <summary>
    /// Desempate DETERMINÍSTICO, o MESMO do ADM-01: vence o maior instante; no empate exato, o maior
    /// identificador na ordem ordinal. Arbitrário de propósito, mas igual em toda execução — duas passadas da
    /// manutenção jamais escolhem fotografias diferentes para o mesmo mês.
    /// </summary>
    private static bool MaisRecenteQue(
        DateTimeOffset candidataEm, Guid candidataId, DateTimeOffset gravadaEm, Guid gravadaId) =>
        candidataEm > gravadaEm || (candidataEm == gravadaEm && candidataId.CompareTo(gravadaId) > 0);

    /// <summary>
    /// O que cada mês tem, apurado com VOLUME LIMITADO por operação: um agregado por mês (as contagens e os
    /// instantes máximos) e UMA leitura das poucas linhas que estão nesses instantes.
    ///
    /// Materializar as aquisições do ano da origem para escolher doze fotografias é ler milhares de linhas
    /// para usar doze. As contagens saem de <c>COUNT</c>, os candidatos saem de <c>MAX</c>, e só as linhas
    /// empatadas no instante máximo vêm para a memória — onde acontece o desempate fino por identificador.
    /// </summary>
    private static async Task<IReadOnlyList<MesCalculado>> CalcularMesesAsync(
        AegisScoreDbContext db,
        IdentityAdmDirectoryKey dir,
        IReadOnlyList<DateOnly> window,
        CancellationToken ct)
    {
        var agregados = new Dictionary<DateOnly, MesAgregado>(window.Count);

        foreach (var mes in window)
        {
            var inicio = IdentityAdmRetentionPolicy.StartOf(mes).UtcDateTime;
            var fim = IdentityAdmRetentionPolicy.EndOf(mes).UtcDateTime;

            var agregado = await db.IdentityAcquisitions.AsNoTracking()
                .Where(a => a.Provider == dir.Provider && a.DirectoryNamespace == dir.DirectoryNamespace
                            && a.AcquiredAtUtc >= inicio && a.AcquiredAtUtc < fim)
                .GroupBy(a => 1)
                .Select(g => new MesAgregado(
                    g.Count(),
                    g.Count(a => a.State == KnightSourceState.Completed
                                 || a.State == KnightSourceState.PartialCollection),
                    g.Max(a => (DateTime?)a.AcquiredAtUtc),
                    g.Max(a => a.State == KnightSourceState.Completed
                               || a.State == KnightSourceState.PartialCollection
                        ? (DateTime?)a.AcquiredAtUtc
                        : null)))
                .FirstOrDefaultAsync(ct);

            agregados[mes] = agregado ?? MesAgregado.Vazio;
        }

        var instantes = agregados.Values
            .SelectMany(a => new[] { a.LastAt, a.LastDataProducingAt })
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();

        var linhas = instantes.Count == 0
            ? new List<AquisicaoResumo>()
            : await db.IdentityAcquisitions.AsNoTracking()
                .Where(a => a.Provider == dir.Provider && a.DirectoryNamespace == dir.DirectoryNamespace
                            && instantes.Contains(a.AcquiredAtUtc))
                .Select(a => new AquisicaoResumo(
                    a.Id, a.AcquiredAt, a.AcquiredAtUtc, a.ObservedAt, a.ConnectorConfigId, a.SourceLabel,
                    a.SchemaVersion, a.NormalizationVersion, a.State, a.Detail))
                .ToListAsync(ct);

        var resultado = new List<MesCalculado>(window.Count);
        foreach (var mes in window)
        {
            var agregado = agregados[mes];

            var snapshot = agregado.LastDataProducingAt is { } fotoEm
                ? Desempatar(linhas.Where(l => l.AcquiredAtUtc == fotoEm && l.ProducedData))
                : null;

            var ultima = agregado.LastAt is { } tentativaEm
                ? Desempatar(linhas.Where(l => l.AcquiredAtUtc == tentativaEm))
                : null;

            resultado.Add(new MesCalculado(
                mes, agregado.Count, agregado.DataProducingCount, snapshot, ultima));
        }

        return resultado;
    }

    private static AquisicaoResumo? Desempatar(IEnumerable<AquisicaoResumo> empatadas) =>
        empatadas.OrderBy(a => a.AcquiredAt).ThenBy(a => a.Id).LastOrDefault();

    /// <summary>
    /// Os meses cuja consolidação está GRAVADA com o instante desta passada e a versão corrente da regra. É a
    /// releitura do banco que autoriza a remoção — e ela é por MÊS, porque a pergunta que a retenção faz é
    /// "este mês, o desta candidata, já incorporou o que estou prestes a remover?".
    /// </summary>
    private static async Task<HashSet<DateOnly>> ConfirmarConsolidacaoAsync(
        AegisScoreDbContext db,
        IdentityAdmDirectoryKey dir,
        IReadOnlyList<DateOnly> window,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var primeiroMes = window[0];
        var ultimoMes = window[window.Count - 1];
        var versao = IdentityAdmRetentionPolicy.ConsolidationVersion;

        var meses = await db.IdentityMonthlyRollups.AsNoTracking()
            .Where(r => r.Provider == dir.Provider && r.DirectoryNamespace == dir.DirectoryNamespace
                        && r.Month >= primeiroMes && r.Month <= ultimoMes
                        && r.ConsolidatedAt == now && r.ConsolidationVersion == versao)
            .Select(r => r.Month)
            .ToListAsync(ct);

        return meses.ToHashSet();
    }

    // ---- Retenção -------------------------------------------------------------------------------------

    private static async Task<(IdentityAdmDirectoryReport Report, bool BatchExhausted)> RetainAsync(
        AegisScoreDbContext db,
        IdentityAdmDirectoryKey dir,
        DateTimeOffset now,
        DateTimeOffset detailCutoff,
        DateOnly oldestMonth,
        IdentityAdmMaintenanceRequest request,
        Dictionary<DateOnly, IdentityMonthlyRollup> rollups,
        HashSet<DateOnly> confirmados,
        CancellationToken ct)
    {
        var protegidas = new List<IdentityAdmProtectedAcquisition>();
        var detalheExpirado = 0;
        var removidas = 0;
        var observacoesRemovidas = 0;

        // Candidatos e referências lidos DENTRO da transação, com as linhas travadas. A pergunta "alguém ainda
        // depende desta aquisição?" respondida antes da transação é uma foto vencida: a avaliação que passaria
        // a citá-la pode acontecer no intervalo entre a resposta e o DELETE.
        var (lote, referencias, esgotouLote) = await CandidatasAsync(db, dir, detailCutoff, request, ct);

        foreach (var candidata in lote)
        {
            ct.ThrowIfCancellationRequested();

            var mes = IdentityAdmRetentionPolicy.MonthOf(candidata.AcquiredAt);

            // PRÉ-CONDIÇÃO por candidata. Meses ANTERIORES à janela de 12 meses não têm consolidação a dever:
            // não há histórico a preservar ali, e a linha mensal correspondente sai nesta mesma passada.
            if (mes >= oldestMonth && !confirmados.Contains(mes))
            {
                protegidas.Add(new IdentityAdmProtectedAcquisition(
                    candidata.Id, candidata.AcquiredAt, MotivoConsolidacaoNaoConfirmada(mes)));
                continue;
            }

            if (referencias.TryGetValue(candidata.Id, out var motivo))
            {
                // EXCEÇÃO declarada: a evidência que sustenta uma avaliação ou o cadastro atual fica além dos
                // 90 dias. O que sai é só o DETALHE OPERACIONAL; cabeçalho, fatos, capacidades e completude
                // por conjunto ficam — é com eles que a avaliação se reproduz, e sem eles o veredito perderia
                // a base. Os objetos afetados daquele veredito estão congelados em outra tabela e não são
                // tocados por nenhum caminho daqui.
                protegidas.Add(new IdentityAdmProtectedAcquisition(candidata.Id, candidata.AcquiredAt, motivo));

                if (candidata.DetailRetiredAt is not null) continue;

                observacoesRemovidas += await db.IdentityEntityObservations
                    .Where(o => o.AcquisitionId == candidata.Id)
                    .ExecuteDeleteAsync(ct);

                var aquisicao = await db.IdentityAcquisitions.FirstAsync(a => a.Id == candidata.Id, ct);
                aquisicao.DetailRetiredAt = now;
                detalheExpirado++;
                continue;
            }

            // Sem referência alguma no produto: sai por INTEIRO. Remover só as observações e manter o cabeçalho
            // não limitaria o crescimento — os cabeçalhos e os fatos agregados também se acumulam por coleta.
            observacoesRemovidas += await db.IdentityEntityObservations
                .Where(o => o.AcquisitionId == candidata.Id)
                .ExecuteDeleteAsync(ct);

            var vencida = await db.IdentityAcquisitions.FirstAsync(a => a.Id == candidata.Id, ct);
            db.IdentityAcquisitions.Remove(vencida);   // cascata remove os estados por conjunto
            removidas++;

            RegistrarRemocaoNoMes(rollups, mes, candidata);
        }

        // Consolidações fora da janela de 12 meses. Entidades canônicas e vínculos NÃO entram em nenhuma
        // remoção: ausência numa população não é exclusão de identidade, e apagar a identidade destruiria a
        // correlação entre observações de conjuntos e de meses diferentes.
        var vencidasDoHistorico = await db.IdentityMonthlyRollups
            .Where(r => r.Provider == dir.Provider && r.DirectoryNamespace == dir.DirectoryNamespace
                        && r.Month < oldestMonth)
            .ToListAsync(ct);

        var consolidacoesRemovidas = 0;
        if (vencidasDoHistorico.Count > 0)
        {
            db.IdentityMonthlyRollups.RemoveRange(vencidasDoHistorico);   // cascata remove os conjuntos
            consolidacoesRemovidas = vencidasDoHistorico.Count;
        }

        await db.SaveChangesAsync(ct);

        return (new IdentityAdmDirectoryReport(
            dir, 0, consolidacoesRemovidas, lote.Count, detalheExpirado, removidas, observacoesRemovidas,
            await ProtegidasRetidasAsync(db, dir, detailCutoff, ct), protegidas), esgotouLote);
    }

    /// <summary>
    /// Contabiliza no MÊS a aquisição que saiu por inteiro, e avança a fronteira do que a retenção já varreu.
    ///
    /// Os dois registros existem por motivos diferentes. As contagens conservam o denominador histórico do mês
    /// (a linha não existe mais e não pode ser recontada). A fronteira é a memória BOUNDED que impede o
    /// repovoamento silencioso: sem ela, reapresentar o identificador removido cairia no caminho de criação e
    /// recriaria a evidência expurgada — e guardar a lista de identificadores removidos cresceria para sempre,
    /// que é o oposto do propósito de uma retenção.
    /// </summary>
    private static void RegistrarRemocaoNoMes(
        Dictionary<DateOnly, IdentityMonthlyRollup> rollups, DateOnly mes, CandidataResumo candidata)
    {
        // Mês fora da janela de 12 meses não tem consolidação (ela sai nesta mesma passada): não há onde
        // registrar, e também não há histórico a proteger ali. Limite declarado, não furo silencioso.
        if (!rollups.TryGetValue(mes, out var rollup)) return;

        rollup.RetiredAcquisitionCount++;
        if (candidata.ProducedData) rollup.RetiredDataProducingCount++;

        if (rollup.RetentionSweptThroughAt is null || candidata.AcquiredAt > rollup.RetentionSweptThroughAt)
            rollup.RetentionSweptThroughAt = candidata.AcquiredAt;
    }

    /// <summary>
    /// SIMULAÇÃO: apura o que a passada real faria e NÃO escreve nada — nem consolidação, nem remoção, nem
    /// transação, nem trava. Travar o diretório de todas as origens só para contar meses atrapalharia a coleta
    /// real sem proteger nada.
    ///
    /// A prévia mostra candidatos, protegidos e motivos. Ela não confirma consolidação (nada foi gravado), e
    /// por isso reporta o que a execução real removeria DEPOIS de consolidar — que é a pergunta que quem
    /// simula está fazendo.
    /// </summary>
    private static async Task<(IdentityAdmDirectoryReport Report, bool BatchExhausted)> SimulateAsync(
        AegisScoreDbContext db,
        IdentityAdmDirectoryKey dir,
        IReadOnlyList<DateOnly> window,
        DateTimeOffset detailCutoff,
        DateOnly oldestMonth,
        IdentityAdmMaintenanceRequest request,
        CancellationToken ct)
    {
        var mesesConsolidados = request.Consolidate ? (await CalcularMesesAsync(db, dir, window, ct)).Count : 0;

        if (!request.Remove)
            return (new IdentityAdmDirectoryReport(
                dir, mesesConsolidados, 0, 0, 0, 0, 0,
                await ProtegidasRetidasAsync(db, dir, detailCutoff, ct),
                Array.Empty<IdentityAdmProtectedAcquisition>()), false);

        var (lote, referencias, esgotou) = await CandidatasAsync(db, dir, detailCutoff, request, ct);

        var protegidas = new List<IdentityAdmProtectedAcquisition>();
        var detalheExpirado = 0;
        var removidas = 0;
        var perderiamDetalhe = new List<Guid>();

        foreach (var c in lote)
        {
            if (referencias.TryGetValue(c.Id, out var motivo))
            {
                protegidas.Add(new IdentityAdmProtectedAcquisition(c.Id, c.AcquiredAt, motivo));
                if (c.DetailRetiredAt is null) { detalheExpirado++; perderiamDetalhe.Add(c.Id); }
            }
            else
            {
                removidas++;
                perderiamDetalhe.Add(c.Id);
            }
        }

        var consolidacoesRemovidas = await db.IdentityMonthlyRollups.AsNoTracking()
            .CountAsync(r => r.Provider == dir.Provider && r.DirectoryNamespace == dir.DirectoryNamespace
                             && r.Month < oldestMonth, ct);

        var observacoes = perderiamDetalhe.Count == 0
            ? 0
            : await db.IdentityEntityObservations.AsNoTracking()
                .CountAsync(o => perderiamDetalhe.Contains(o.AcquisitionId), ct);

        return (new IdentityAdmDirectoryReport(
            dir, mesesConsolidados, consolidacoesRemovidas, lote.Count, detalheExpirado, removidas, observacoes,
            await ProtegidasRetidasAsync(db, dir, detailCutoff, ct), protegidas), esgotou);
    }

    /// <summary>
    /// Quantas aquisições vencidas desta origem CONTINUAM aqui por referência — inclusive as que já tiveram o
    /// detalhe expirado em passadas anteriores e por isso não voltam à lista de candidatas. Sem esta contagem,
    /// um relatório com "0 protegidas" seria lido como "nada além dos 90 dias", que é o contrário do que
    /// acontece: a evidência que sustenta uma avaliação, ou o cadastro atual, fica.
    /// </summary>
    private static Task<int> ProtegidasRetidasAsync(
        AegisScoreDbContext db, IdentityAdmDirectoryKey dir, DateTimeOffset detailCutoff, CancellationToken ct)
    {
        var corte = detailCutoff.UtcDateTime;

        return db.IdentityAcquisitions.AsNoTracking()
            .CountAsync(a => a.Provider == dir.Provider && a.DirectoryNamespace == dir.DirectoryNamespace
                             && a.AcquiredAtUtc < corte
                             && (db.KnightAssessmentRuns.Any(r => r.IdentityAcquisitionId == a.Id)
                                 || db.IdentityEntities.Any(e => e.CurrentAcquisitionId == a.Id)
                                 || db.IdentitySourceLinks.Any(l => l.LastAcquisitionId == a.Id)), ct);
    }

    private const string MotivoAvaliacao =
        "Referenciada por uma avaliação do AEGIS KNIGHT: o cabeçalho, os fatos e a completude por conjunto são "
        + "necessários para reproduzir aquele veredito e ficam preservados além dos 90 dias.";

    private const string MotivoProjecao =
        "Sustenta o cadastro ATUAL do ADM (a aquisição que estabeleceu os atributos de uma identidade canônica "
        + "ou a última observação de um vínculo de origem): removê-la por inteiro deixaria a projeção "
        + "apontando para uma coleta inexistente.";

    private static string MotivoConsolidacaoNaoConfirmada(DateOnly mes) =>
        $"Preservada por precaução: a consolidação de {mes:yyyy-MM} não foi confirmada nesta passada, e remover "
        + "evidência que o histórico ainda não incorporou a destruiria sem possibilidade de recálculo.";

    /// <summary>
    /// Os candidatos DESTA passada, já travados, e o MOTIVO de proteção de cada um que ainda é referenciado.
    ///
    /// As referências conferidas são três, e nenhuma é opcional: a avaliação do KNIGHT que CITA a aquisição, a
    /// identidade canônica cujos atributos atuais vieram dela, e o vínculo de origem cuja última observação
    /// foi ela. Conferir apenas a primeira removeria por inteiro a coleta que sustenta o cadastro atual — e o
    /// cadastro passaria a apontar para o vazio.
    ///
    /// O filtro exclui as aquisições que já estão no estado FINAL (detalhe expirado E ainda referenciadas por
    /// qualquer um dos três): sem isso, elas consumiriam o lote em toda passada e as candidatas mais novas
    /// nunca chegariam a ser examinadas — o expurgo pararia de convergir sem nunca falhar.
    /// </summary>
    private static async Task<(List<CandidataResumo> Lote, Dictionary<Guid, string> Referencias, bool BatchExhausted)>
        CandidatasAsync(
            AegisScoreDbContext db,
            IdentityAdmDirectoryKey dir,
            DateTimeOffset detailCutoff,
            IdentityAdmMaintenanceRequest request,
            CancellationToken ct)
    {
        var corte = detailCutoff.UtcDateTime;

        var pendentes = db.IdentityAcquisitions.AsNoTracking()
            .Where(a => a.Provider == dir.Provider && a.DirectoryNamespace == dir.DirectoryNamespace
                        && a.AcquiredAtUtc < corte)
            .Where(a => a.DetailRetiredAt == null
                        || !(db.KnightAssessmentRuns.Any(r => r.IdentityAcquisitionId == a.Id)
                             || db.IdentityEntities.Any(e => e.CurrentAcquisitionId == a.Id)
                             || db.IdentitySourceLinks.Any(l => l.LastAcquisitionId == a.Id)))
            .OrderBy(a => a.AcquiredAtUtc).ThenBy(a => a.Id);

        // Pede UM a mais que o teto: é assim que se sabe que sobrou trabalho sem contar a tabela inteira.
        var lote = await pendentes
            .Select(a => new CandidataResumo(a.Id, a.AcquiredAt, a.DetailRetiredAt, a.State))
            .Take(request.MaxAcquisitionsPerDirectory + 1)
            .ToListAsync(ct);

        var esgotou = lote.Count > request.MaxAcquisitionsPerDirectory;
        if (esgotou) lote.RemoveAt(lote.Count - 1);

        var ids = lote.Select(c => c.Id).ToList();
        if (ids.Count == 0) return (lote, new Dictionary<Guid, string>(), esgotou);

        if (!request.Simulate) await LockAcquisitionsAsync(db, ids, dir.TenantId, ct);

        var porAvaliacao = await db.KnightAssessmentRuns.AsNoTracking()
            .Where(r => r.IdentityAcquisitionId != null && ids.Contains(r.IdentityAcquisitionId.Value))
            .Select(r => r.IdentityAcquisitionId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var porEntidade = await db.IdentityEntities.AsNoTracking()
            .Where(e => ids.Contains(e.CurrentAcquisitionId))
            .Select(e => e.CurrentAcquisitionId)
            .Distinct()
            .ToListAsync(ct);

        var porVinculo = await db.IdentitySourceLinks.AsNoTracking()
            .Where(l => ids.Contains(l.LastAcquisitionId))
            .Select(l => l.LastAcquisitionId)
            .Distinct()
            .ToListAsync(ct);

        var referencias = new Dictionary<Guid, string>();
        foreach (var id in porAvaliacao) referencias[id] = MotivoAvaliacao;
        foreach (var id in porEntidade.Concat(porVinculo).Distinct())
            referencias[id] = referencias.TryGetValue(id, out var ja)
                ? ja + " " + MotivoProjecao
                : MotivoProjecao;

        return (lote, referencias, esgotou);
    }

    // ---- Travas ---------------------------------------------------------------------------------------

    /// <summary>
    /// A MESMA trava consultiva que a coleta toma (<see cref="IdentityDirectoryLock"/>). É por ela que coleta,
    /// consolidação e retenção se serializam; chaves diferentes fariam três caminhos travarem três coisas e
    /// nunca se encontrarem.
    /// </summary>
    private static async Task LockDirectoryAsync(
        AegisScoreDbContext db, IdentityAdmDirectoryKey dir, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;   // SQLite serializa escritores na própria conexão

        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "A manutenção do ADM exige transação aberta antes da trava: no autocommit ela seria liberada "
                + "imediatamente e a seção crítica deixaria de existir.");

        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            new object[] { IdentityDirectoryLock.AdvisoryKey(dir.TenantId, dir.DirectoryNamespace) }, ct);
    }

    /// <summary>
    /// Trava EXCLUSIVA das linhas candidatas. Conflita com a trava compartilhada que a avaliação toma ao citar
    /// uma aquisição (<c>PinForReferenceAsync</c>): sem esse par, retenção e referenciamento decidiriam sobre a
    /// mesma foto antiga, e a avaliação apontaria para uma coleta já removida.
    ///
    /// Ordenada por identificador para que duas passadas concorrentes travem as mesmas linhas na mesma ordem —
    /// espera, e não impasse.
    /// </summary>
    private static async Task LockAcquisitionsAsync(
        AegisScoreDbContext db, IReadOnlyList<Guid> ids, Guid tenantId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;

        await db.Database
            .SqlQueryRaw<int>(
                "SELECT 1 AS \"Value\" FROM \"IdentityAcquisitions\" "
                + "WHERE \"Id\" = ANY({0}) AND \"TenantId\" = {1} ORDER BY \"Id\" FOR UPDATE",
                new object[] { ids.ToArray(), tenantId })
            .ToListAsync(ct);
    }

    // ---- Tipos internos -------------------------------------------------------------------------------

    /// <summary>Projeção da origem para a paginação no banco — tipo NOMEADO porque o UNION exige.</summary>
    private sealed record OrigemLinha(Guid TenantId, KnightSourceType Provider, string DirectoryNamespace);

    /// <summary>O que o banco agrega de um mês: as contagens e os instantes máximos (todas e com dados).</summary>
    private sealed record MesAgregado(
        int Count, int DataProducingCount, DateTime? LastAt, DateTime? LastDataProducingAt)
    {
        public static readonly MesAgregado Vazio = new(0, 0, null, null);
    }

    private sealed record AquisicaoResumo(
        Guid Id,
        DateTimeOffset AcquiredAt,
        DateTime AcquiredAtUtc,
        DateTimeOffset? ObservedAt,
        Guid ConnectorConfigId,
        string SourceLabel,
        string SchemaVersion,
        string NormalizationVersion,
        KnightSourceState State,
        string? Detail)
    {
        public bool ProducedData =>
            State is KnightSourceState.Completed or KnightSourceState.PartialCollection;
    }

    private sealed record CandidataResumo(
        Guid Id, DateTimeOffset AcquiredAt, DateTimeOffset? DetailRetiredAt, KnightSourceState State)
    {
        public bool ProducedData =>
            State is KnightSourceState.Completed or KnightSourceState.PartialCollection;
    }

    /// <summary>
    /// Um mês já apurado: as contagens das linhas SOBREVIVENTES (as removidas ficam contadas na própria
    /// consolidação) e as candidatas a fotografia e a última tentativa.
    /// </summary>
    private sealed record MesCalculado(
        DateOnly Month,
        int SurvivingCount,
        int SurvivingDataProducingCount,
        AquisicaoResumo? Snapshot,
        AquisicaoResumo? LastAttempt);
}
