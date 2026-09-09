using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity.Adm;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Identity;

/// <summary>
/// [AEGIS-ADM-01] Persistência e RECONCILIAÇÃO do ADM de identidade.
///
/// Cinco coisas que este componente existe para manter distintas — e que uma tabela ingênua colapsaria:
///   • RETRY da mesma aquisição × aquisição NOVA. O retry reaproveita o mesmo registro (mesmo id, mesmo
///     conteúdo) e não reescreve nada; uma coleta nova é sempre uma linha nova, mesmo quando observa
///     exatamente o mesmo conteúdo. Dois fingerprints iguais são um FATO interessante, não motivo para
///     deduplicar. Reapresentar um id com conteúdo DIFERENTE é conflito, não atualização.
///   • EVIDÊNCIA × PROJEÇÃO. As observações guardam os atributos COMO OBSERVADOS, por conjunto, e não são
///     reescritas; a entidade canônica guarda o estado ATUAL e é atualizada com ordenação determinística —
///     uma coleta ATRASADA nunca sobrescreve um estado mais recente.
///   • CONJUNTO × CONJUNTO. O mesmo objeto observado em duas populações produz DUAS observações, cada uma
///     com a constatação que o colocou naquela população. Compartilhar a entidade não é compartilhar a prova.
///   • CONJUNTO VAZIO × CONJUNTO DESCONHECIDO. Cada conjunto tem estado próprio: uma falha em um deles não
///     apaga nem invalida os outros, e um conjunto não coletado NÃO afirma ausência de objetos.
///   • ORIGEM × CONECTOR. A identidade do objeto vive no NAMESPACE do diretório. Apontar a configuração para
///     outro diretório produz vínculos novos — jamais reaproveita os do diretório anterior.
///
/// Isolamento: Global Query Filter na leitura, stamping de tenant no SaveChanges e FKs COMPOSTAS
/// (Id, TenantId) no banco. Um tenant estrangeiro não lê, não vincula e não escreve.
/// </summary>
public sealed class IdentityAcquisitionStore : IIdentityAcquisitionStore
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;

    public IdentityAcquisitionStore(AegisScoreDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <inheritdoc />
    public async Task LockOriginAsync(IdentityAcquisitionOrigin origin, CancellationToken ct = default)
    {
        if (origin is null) throw new ArgumentNullException(nameof(origin));

        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException("Trava de gravação do ADM sem tenant resolvido no contexto (fail-closed).");

        var ns = (origin.DirectoryNamespace ?? "").Trim();
        if (ns.Length == 0)
            throw new InvalidOperationException(
                "Trava de gravação do ADM sem namespace de diretório: sem ele não há escopo de serialização.");

        if (!_db.Database.IsNpgsql())
        {
            // SQLite (bateria relacional) serializa escritores na própria conexão: não há duas transações
            // concorrentes para ordenar. Declarado em vez de silenciado — a proteção REAL é a do PostgreSQL,
            // e é lá que ela é exercitada.
            return;
        }

        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "A trava de gravação do ADM exige uma transação aberta: fora dela o autocommit a liberaria "
                + "imediatamente, e a seção crítica de ler → decidir → gravar deixaria de existir.");

        // 1) DIRETÓRIO. Trava consultiva de TRANSAÇÃO (liberada no commit/rollback, nunca esquecida) sobre
        //    (tenant, namespace) — o escopo em que vivem as entidades canônicas e os vínculos, e que pode ser
        //    disputado por conectores diferentes apontados para o MESMO diretório.
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})", new object[] { AdvisoryKey(tenantId, ns) }, ct);

        // 2) CONECTOR. Trava de LINHA sobre a configuração — o escopo do snapshot agregado, da última
        //    tentativa e da saúde da integração. Sempre DEPOIS da trava do diretório: a ordem fixa é o que
        //    impede que dois escritores em sentidos opostos se travem mutuamente.
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"Connectors\" WHERE \"Id\" = {0} AND \"TenantId\" = {1} FOR UPDATE",
            new object[] { origin.ConnectorConfigId, tenantId }, ct);
    }

    public async Task PrepareAsync(IdentityAcquisitionRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException("Aquisição de identidade sem tenant resolvido no contexto (fail-closed).");

        // GUARD de origem. Dados de demonstração não podem se misturar aos objetos reais do ADM, e um
        // namespace vazio tornaria a chave de identidade ambígua — dois diretórios diferentes colapsariam num
        // só. Nos dois casos a recusa é explícita: silenciar aqui corromperia a identidade de forma permanente.
        if (request.Origin.Provider == KnightSourceType.Demo)
            throw new InvalidOperationException(
                "A fonte de demonstração não produz objetos do ADM: dados sintéticos não podem se misturar às "
                + "identidades reais do tenant.");

        var ns = (request.Origin.DirectoryNamespace ?? "").Trim();
        if (ns.Length == 0)
            throw new InvalidOperationException(
                "Aquisição sem namespace de diretório resolvido: sem ele não há como delimitar o espaço de "
                + "identificadores, e objetos de diretórios distintos seriam unificados indevidamente.");

        // Instante de aquisição NORMALIZADO (UTC, microssegundos — a resolução real do banco). Um único valor
        // atravessa a aquisição, os vínculos, as observações e a projeção: se cada um guardasse a sua própria
        // versão do mesmo instante, um retry legítimo passaria a divergir de si mesmo depois da primeira
        // ida-e-volta ao PostgreSQL, e a ordenação determinística deixaria de ser determinística.
        var acquiredAt = IdentityAcquisitionContent.NormalizeInstant(request.AcquiredAt);

        // ---- 1) A AQUISIÇÃO. Imutável depois de gravada; retry idempotente; conteúdo diverso = conflito. ---
        // A verificação acontece ANTES de qualquer escrita — um conflito não pode deixar metade da evidência
        // nova encostada na aquisição antiga.
        var fingerprint = IdentityAcquisitionContent.ObservedFingerprint(request);

        var acquisition = await _db.IdentityAcquisitions
            .Include(a => a.Sets)
            .FirstOrDefaultAsync(a => a.Id == request.AcquisitionId, ct);

        if (acquisition is null)
        {
            acquisition = new IdentityAcquisition
            {
                Id = request.AcquisitionId,
                ConnectorConfigId = request.Origin.ConnectorConfigId,
                Provider = request.Origin.Provider,
                DirectoryNamespace = ns,
                SourceLabel = request.Origin.SourceLabel,
                SchemaVersion = request.SchemaVersion,
                NormalizationVersion = request.NormalizationVersion,
                AcquiredAt = acquiredAt,
                ObservedAt = request.ObservedAt is { } o ? IdentityAcquisitionContent.NormalizeInstant(o) : null,
                State = request.State,
                Detail = request.Detail,
                FactsJson = request.FactsJson,
                CapabilitiesJson = request.CapabilitiesJson,
                ContentFingerprint = fingerprint,
            };
            _db.IdentityAcquisitions.Add(acquisition);
        }
        else
        {
            // Uma avaliação já pode citar esta aquisição. Aceitar conteúdo novo sob o mesmo identificador
            // faria a prova de uma avaliação publicada mudar depois de publicada.
            var divergencias = Divergencias(acquisition, request, ns, fingerprint);
            if (divergencias.Count > 0)
                throw new IdentityAcquisitionConflictException(request.AcquisitionId, divergencias);
        }
        // Reapresentação COMPATÍVEL: nenhum campo escalar é reescrito — nem origem, nem versões, nem horários,
        // nem fatos. A reconciliação abaixo é ADITIVA e converge para o mesmo estado.

        // ---- 2) O que cada CONJUNTO observou, e o que a PROJEÇÃO deve refletir -----------------------------
        // Os conjuntos são percorridos na ordem CANÔNICA do enum, nunca na ordem incidental em que a coleta
        // os devolveu: é o que faz o resultado não depender de como a lista chegou.
        var setsOrdenados = (request.Sets ?? Array.Empty<IdentityObservedSet>())
            .OrderBy(s => (int)s.Set)
            .ToList();

        // Conteúdo da observação: por (CONJUNTO, identificador externo). O mesmo objeto costuma aparecer em
        // duas populações com constatações e papéis DIFERENTES — é justamente a diferença que explica por que
        // ele entrou em cada uma. Um dicionário único por identificador colapsaria as duas provas na última
        // que a lista trouxesse.
        var objetosPorConjunto = new Dictionary<IdentityObservationSet, IReadOnlyList<IdentityObservedObject>>();
        foreach (var set in setsOrdenados)
            objetosPorConjunto[set.Set] = IdentityAcquisitionContent.Canonical(set);

        // Atributos da PROJEÇÃO: precedência estável e explícita, resolvida atributo a atributo. O primeiro
        // conjunto (em ordem canônica) que INFORMA um atributo vence; conjuntos posteriores que o omitem não
        // o apagam nem inventam substituto. Assim, inverter a ordem dos conjuntos na coleta não muda o
        // cadastro atual, e um conjunto que só traz o identificador não empobrece a entidade.
        var projecao = new Dictionary<string, AtributosProjetados>(StringComparer.Ordinal);
        foreach (var set in setsOrdenados)
        {
            foreach (var observed in objetosPorConjunto[set.Set])
            {
                projecao.TryGetValue(observed.ExternalId, out var atual);
                projecao[observed.ExternalId] = (atual ?? AtributosProjetados.Vazio).Com(observed);
            }
        }

        // ORDEM ORDINAL, e não a ordem em que a coleta devolveu os objetos. Duas gravações do MESMO diretório
        // tocam as mesmas linhas; se cada uma as tocasse numa ordem diferente, o PostgreSQL detectaria
        // deadlock em vez de simplesmente serializá-las. Ordem estável = espera, não impasse.
        var externalIds = projecao.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

        // ---- 3) Resolução das ENTIDADES pelos vínculos de ORIGEM -------------------------------------------
        // Um objeto observado em DOIS conjuntos é resolvido UMA vez: a chave é (tenant, namespace, id
        // externo), nunca o nome nem o UPN. É esta consulta única que faz duas observações apontarem para uma
        // entidade — e que jamais funde homônimos ou convidados B2B de diretórios diferentes.
        var links = externalIds.Count == 0
            ? new List<IdentitySourceLink>()
            : await _db.IdentitySourceLinks
                .Where(l => l.DirectoryNamespace == ns && externalIds.Contains(l.ExternalId))
                .ToListAsync(ct);

        var linkByExternalId = links.ToDictionary(l => l.ExternalId, StringComparer.Ordinal);

        // Entidades resolvidas NESTA chamada, indexadas pelo identificador externo. O mesmo objeto observado
        // em dois conjuntos precisa reencontrar a entidade que o PRIMEIRO conjunto criou e que ainda não foi
        // salva — buscá-la no banco não a acharia, e criar outra quebraria a invariante central do modelo.
        var entityCache = new Dictionary<Guid, IdentityEntity>();
        var entityByExternalId = new Dictionary<string, IdentityEntity>(StringComparer.Ordinal);

        foreach (var externalId in externalIds)
        {
            var atributos = projecao[externalId];

            if (linkByExternalId.TryGetValue(externalId, out var link))
            {
                // Vínculo existente: a MESMA entidade, mesmo que o nome tenha mudado desde a última coleta.
                entityByExternalId[externalId] = await ResolveEntityAsync(entityCache, link.IdentityEntityId, ct);

                // Uma coleta ATRASADA continua sendo evidência, mas não redefine "quando foi visto por
                // último" nem de que aquisição vem a proveniência corrente do vínculo.
                if (acquiredAt < link.FirstLinkedAt) link.FirstLinkedAt = acquiredAt;

                if (MaisRecente(acquiredAt, request.AcquisitionId, link.LastObservedAt, link.LastAcquisitionId))
                {
                    link.ConnectorConfigId = request.Origin.ConnectorConfigId;
                    link.Provider = request.Origin.Provider;
                    link.LastObservedAt = acquiredAt;
                    link.LastAcquisitionId = request.AcquisitionId;
                }
                continue;
            }

            // Objeto novo NESTE namespace. Um identificador igual em outro namespace já produziu outra
            // entidade — e continua produzindo, porque o namespace faz parte da chave.
            var entity = new IdentityEntity
            {
                Kind = atributos.Kind ?? IdentityEntityKind.Unknown,
                DisplayName = atributos.DisplayName,
                UserPrincipalName = atributos.UserPrincipalName,
                FirstObservedAt = acquiredAt,
                LastObservedAt = acquiredAt,
                CurrentAsOf = acquiredAt,
                CurrentAcquisitionId = request.AcquisitionId,
            };
            _db.IdentityEntities.Add(entity);

            _db.IdentitySourceLinks.Add(new IdentitySourceLink
            {
                IdentityEntityId = entity.Id,
                ConnectorConfigId = request.Origin.ConnectorConfigId,
                Provider = request.Origin.Provider,
                DirectoryNamespace = ns,
                ExternalId = externalId,
                FirstLinkedAt = acquiredAt,
                LastObservedAt = acquiredAt,
                LastAcquisitionId = request.AcquisitionId,
            });

            entityCache[entity.Id] = entity;
            entityByExternalId[externalId] = entity;
        }

        // ---- 4) OBSERVAÇÕES: a evidência daquela aquisição, naquele conjunto ------------------------------
        // Observações já gravadas para ESTA aquisição — o que torna o retry idempotente.
        var existingObservations = await _db.IdentityEntityObservations
            .Where(o => o.AcquisitionId == request.AcquisitionId)
            .ToListAsync(ct);
        var observationByKey = existingObservations.ToDictionary(o => (o.IdentityEntityId, o.Set));

        var setStateByKey = acquisition.Sets.ToDictionary(s => s.Set);

        foreach (var set in setsOrdenados)
        {
            var preserved = 0;

            foreach (var observed in objetosPorConjunto[set.Set])
            {
                var entity = entityByExternalId[observed.ExternalId];

                if (observationByKey.ContainsKey((entity.Id, set.Set)))
                {
                    // Retry da MESMA aquisição sobre o MESMO conjunto: a evidência já está registrada e não é
                    // reescrita. Reescrevê-la permitiria que um reprocessamento alterasse o passado.
                    preserved++;
                    continue;
                }

                // O conteúdo vem do objeto COMO ESTE conjunto o observou — a constatação, os papéis e os
                // atributos daquela população, e não os do objeto "vencedor" de outra lista.
                var observation = new IdentityEntityObservation
                {
                    AcquisitionId = request.AcquisitionId,
                    IdentityEntityId = entity.Id,
                    Set = set.Set,
                    ExternalId = observed.ExternalId,
                    Kind = observed.Kind,
                    DisplayNameObserved = observed.DisplayName,
                    UserPrincipalNameObserved = observed.UserPrincipalName,
                    RolesObserved = observed.Roles?.ToList() ?? new List<string>(),
                    Detail = observed.Detail,
                    ObservedAt = acquiredAt,
                };
                _db.IdentityEntityObservations.Add(observation);
                observationByKey[(entity.Id, set.Set)] = observation;
                preserved++;
            }

            // ---- ESTADO/COMPLETUDE do conjunto -----------------------------------------------------------
            if (!setStateByKey.TryGetValue(set.Set, out var state))
            {
                state = new IdentityObservationSetState { AcquisitionId = request.AcquisitionId, Set = set.Set };
                acquisition.Sets.Add(state);
                setStateByKey[set.Set] = state;
            }

            state.Outcome = set.Outcome;
            state.ObservedCount = set.ObservedCount;
            state.PreservedCount = preserved;
            // A completude declarada só sobrevive se a lista tiver mesmo o tamanho da contagem apurada.
            state.IsComplete = set.IsComplete && preserved == set.ObservedCount;
            state.Limitation = state.IsComplete
                ? set.Limitation
                : Join(set.Limitation, preserved == set.ObservedCount
                    ? null
                    : $"A coleta apurou {set.ObservedCount} objeto(s) e preservou {preserved} — a diferença é "
                      + "declarada em vez de escondida.");
        }

        // ---- 5) PROJEÇÃO do estado atual, com ordenação determinística -------------------------------------
        // Uma MESMA entidade pode ter sido observada em vários conjuntos; a projeção é aplicada UMA vez por
        // entidade, na mesma ordem ordinal usada acima.
        foreach (var externalId in externalIds)
        {
            var entity = entityByExternalId[externalId];
            var atributos = projecao[externalId];

            if (acquiredAt < entity.FirstObservedAt) entity.FirstObservedAt = acquiredAt;
            if (acquiredAt > entity.LastObservedAt) entity.LastObservedAt = acquiredAt;

            // Uma coleta ATRASADA não substitui o estado atual mais recente.
            if (!MaisRecente(acquiredAt, request.AcquisitionId, entity.CurrentAsOf, entity.CurrentAcquisitionId)) continue;

            // Ausência de informação NÃO apaga o que já se sabe: estes conjuntos são populações específicas,
            // não o censo do diretório, e um atributo que esta coleta não trouxe não é um atributo removido.
            var kind = atributos.Kind ?? entity.Kind;
            var displayName = atributos.DisplayName ?? entity.DisplayName;
            var upn = atributos.UserPrincipalName ?? entity.UserPrincipalName;

            var mudou = kind != entity.Kind
                || !string.Equals(displayName, entity.DisplayName, StringComparison.Ordinal)
                || !string.Equals(upn, entity.UserPrincipalName, StringComparison.Ordinal);

            entity.Kind = kind;
            entity.DisplayName = displayName;
            entity.UserPrincipalName = upn;
            entity.CurrentAsOf = acquiredAt;
            entity.CurrentAcquisitionId = request.AcquisitionId;
            // UpdatedAt marca mudança de CADASTRO. Uma coleta que reobserva o mesmo objeto sem novidade não
            // é uma alteração da identidade, e carimbá-la faria toda releitura parecer um cadastro mexido.
            if (mudou) entity.UpdatedAt = acquiredAt;
        }
    }

    public async Task<IdentityAcquisitionRecord?> ReadAsync(Guid acquisitionId, CancellationToken ct = default)
    {
        _ = _tenant.TenantId
            ?? throw new TenantSecurityException("Leitura de aquisição de identidade sem tenant resolvido (fail-closed).");

        // O Global Query Filter torna uma aquisição de OUTRO tenant indistinguível de inexistente.
        var acquisition = await _db.IdentityAcquisitions.AsNoTracking()
            .Include(a => a.Sets)
            .FirstOrDefaultAsync(a => a.Id == acquisitionId, ct);

        if (acquisition is null) return null;

        var observations = await _db.IdentityEntityObservations.AsNoTracking()
            .Where(o => o.AcquisitionId == acquisitionId)
            // Ordem estável entre leituras: o identificador externo é o único critério sempre presente.
            .OrderBy(o => o.Set).ThenBy(o => o.ExternalId)
            .ToListAsync(ct);

        var bySet = observations.GroupBy(o => o.Set).ToDictionary(g => g.Key, g => g.ToList());

        var sets = acquisition.Sets
            .OrderBy(s => (int)s.Set)
            .Select(s => new IdentityObservedSetRecord(
                s.Set, s.Outcome, s.ObservedCount, s.PreservedCount, s.IsComplete, s.Limitation,
                bySet.TryGetValue(s.Set, out var items)
                    ? items.Select(o => new IdentityObservedObjectRecord(
                        o.IdentityEntityId, o.ExternalId, o.Kind, o.DisplayNameObserved,
                        o.UserPrincipalNameObserved, o.RolesObserved, o.Detail)).ToList()
                    : new List<IdentityObservedObjectRecord>()))
            .ToList();

        return new IdentityAcquisitionRecord(
            acquisition.Id,
            acquisition.TenantId,
            new IdentityAcquisitionOrigin(
                acquisition.ConnectorConfigId, acquisition.Provider,
                acquisition.DirectoryNamespace, acquisition.SourceLabel),
            acquisition.SchemaVersion,
            acquisition.NormalizationVersion,
            acquisition.AcquiredAt,
            acquisition.ObservedAt,
            acquisition.State,
            acquisition.Detail,
            acquisition.FactsJson,
            acquisition.CapabilitiesJson,
            acquisition.ContentFingerprint,
            sets);
    }

    // ---- Helpers -------------------------------------------------------------------------------------

    /// <summary>
    /// O que uma reapresentação do MESMO identificador de aquisição pode e não pode diferir.
    ///
    /// Duas famílias de campo, verificadas de formas diferentes de propósito:
    ///   • METADADOS DO ATO — origem (conector, provedor, rótulo), versões de schema e normalização, horário
    ///     de aquisição, horário observado e detalhe da tentativa. São comparados um a um, porque o valor de
    ///     um conflito está em dizer QUAL campo mudou.
    ///   • CONTEÚDO OBSERVADO — diretório, estado, fatos, capacidades, conjuntos, completude, limitações e
    ///     todos os atributos preservados dos objetos. Comparado pelo fingerprint canônico, que é justamente
    ///     a definição executável de "observou a mesma coisa".
    ///
    /// O identificador da aquisição não entra: ele é a IDENTIDADE, não o conteúdo. E a projeção atual da
    /// entidade também não: ela muda com o tempo por definição, e é derivada — não faz parte da evidência.
    /// </summary>
    private static IReadOnlyList<string> Divergencias(
        IdentityAcquisition stored, IdentityAcquisitionRequest request, string ns, string observedFingerprint)
    {
        var divergencias = new List<string>();

        void Comparar<T>(string campo, T gravado, T apresentado)
        {
            if (!EqualityComparer<T>.Default.Equals(gravado, apresentado))
                divergencias.Add($"{campo} (gravado: {Descrever(gravado)}; apresentado: {Descrever(apresentado)})");
        }

        Comparar("conector de origem", stored.ConnectorConfigId, request.Origin.ConnectorConfigId);
        Comparar("provedor", stored.Provider, request.Origin.Provider);
        Comparar("namespace do diretório", stored.DirectoryNamespace, ns);
        Comparar("rótulo da fonte", stored.SourceLabel, request.Origin.SourceLabel);
        Comparar("versão do schema", stored.SchemaVersion, request.SchemaVersion);
        Comparar("versão da normalização", stored.NormalizationVersion, request.NormalizationVersion);
        Comparar("horário de aquisição", stored.AcquiredAt, IdentityAcquisitionContent.NormalizeInstant(request.AcquiredAt));
        Comparar("horário observado na fonte", stored.ObservedAt,
            request.ObservedAt is { } o ? IdentityAcquisitionContent.NormalizeInstant(o) : null);
        Comparar("estado da coleta", stored.State, request.State);
        Comparar("detalhe da tentativa", stored.Detail, request.Detail);

        if (!string.Equals(stored.ContentFingerprint, observedFingerprint, StringComparison.Ordinal))
            divergencias.Add(
                $"conteúdo observado — fatos, capacidades, conjuntos, completude, limitações ou atributos "
                + $"preservados (gravado: {stored.ContentFingerprint}; apresentado: {observedFingerprint})");

        return divergencias;
    }

    private static string Descrever(object? value) => value switch
    {
        null => "ausente",
        DateTimeOffset instante => instante.ToUniversalTime().ToString("O"),
        _ => value.ToString() ?? "ausente",
    };

    /// <summary>
    /// Atributos que a aquisição PROJETA sobre a entidade canônica, resolvidos atributo a atributo. Cada um
    /// guarda o PRIMEIRO valor efetivamente informado (na ordem canônica dos conjuntos); <c>null</c> significa
    /// "esta aquisição não disse nada sobre isso" — e não "isto ficou vazio".
    /// </summary>
    private sealed record AtributosProjetados(
        IdentityEntityKind? Kind, string? DisplayName, string? UserPrincipalName)
    {
        public static readonly AtributosProjetados Vazio = new(null, null, null);

        public AtributosProjetados Com(IdentityObservedObject observed) => new(
            // Unknown é a fonte dizendo "não classifiquei", e não uma classificação. Ele não vence um tipo
            // já informado por outro conjunto, e também não é inventado quando ninguém informou.
            Kind ?? (observed.Kind == IdentityEntityKind.Unknown ? null : observed.Kind),
            DisplayName ?? Texto(observed.DisplayName),
            UserPrincipalName ?? Texto(observed.UserPrincipalName));

        private static string? Texto(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Ordenação DETERMINÍSTICA entre uma aquisição e o estado atual de uma projeção. Mais recente vence; no
    /// empate exato de instante, decide a ordem ordinal do identificador da aquisição — arbitrária de
    /// propósito, mas igual em toda execução, em qualquer ordem de chegada e em qualquer réplica.
    /// </summary>
    private static bool MaisRecente(
        DateTimeOffset acquiredAt, Guid acquisitionId, DateTimeOffset currentAsOf, Guid currentAcquisitionId) =>
        acquiredAt > currentAsOf
        || (acquiredAt == currentAsOf && acquisitionId.CompareTo(currentAcquisitionId) >= 0);

    /// <summary>
    /// Resolve a entidade canônica alvo de um vínculo, primeiro entre as já tocadas por esta aquisição e só
    /// depois no banco. O vínculo é tenant-safe por FK composta, então a entidade tem de existir neste
    /// tenant: a ausência é corrupção relacional, não um caso normal.
    /// </summary>
    private async Task<IdentityEntity> ResolveEntityAsync(
        Dictionary<Guid, IdentityEntity> cache, Guid id, CancellationToken ct)
    {
        if (cache.TryGetValue(id, out var cached)) return cached;

        var entity = await _db.IdentityEntities.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new InvalidOperationException(
                $"Vínculo de origem aponta para a entidade de identidade {id}, que não existe neste tenant.");

        cache[id] = entity;
        return entity;
    }

    private static string? Join(string? a, string? b) =>
        string.Join(" ", new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } s ? s : null;

    /// <summary>
    /// Chave da trava consultiva do diretório: 64 bits DERIVADOS de (tenant, namespace) por SHA-256. Não é o
    /// hash de string do .NET de propósito — aquele é aleatorizado por processo, e duas instâncias da API
    /// escolheriam travas diferentes para o mesmo diretório, ou seja, nenhuma trava.
    /// </summary>
    private static long AdvisoryKey(Guid tenantId, string directoryNamespace)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"aegis-adm-identity|{tenantId:D}|{directoryNamespace}"));
        return BitConverter.ToInt64(bytes, 0);
    }
}
