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
/// Quatro coisas que este componente existe para manter distintas — e que uma tabela ingênua colapsaria:
///   • RETRY da mesma aquisição × aquisição NOVA. O retry reaproveita o mesmo registro (mesmo id) e não
///     duplica nada; uma coleta nova é sempre uma linha nova, mesmo quando observa exatamente o mesmo
///     conteúdo. Dois fingerprints iguais são um FATO interessante, não motivo para deduplicar.
///   • EVIDÊNCIA × PROJEÇÃO. As observações guardam os atributos COMO OBSERVADOS e não são reescritas; a
///     entidade canônica guarda o estado ATUAL e é atualizada com ordenação determinística — uma coleta
///     ATRASADA nunca sobrescreve um estado mais recente.
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

        // ---- 1) A AQUISIÇÃO. Retry da MESMA aquisição reaproveita a linha; coleta nova cria outra. --------
        var acquisition = await _db.IdentityAcquisitions
            .Include(a => a.Sets)
            .FirstOrDefaultAsync(a => a.Id == request.AcquisitionId, ct);

        var fingerprint = Fingerprint(request);

        if (acquisition is null)
        {
            acquisition = new IdentityAcquisition { Id = request.AcquisitionId };
            _db.IdentityAcquisitions.Add(acquisition);
        }

        acquisition.ConnectorConfigId = request.Origin.ConnectorConfigId;
        acquisition.Provider = request.Origin.Provider;
        acquisition.DirectoryNamespace = ns;
        acquisition.SourceLabel = request.Origin.SourceLabel;
        acquisition.SchemaVersion = request.SchemaVersion;
        acquisition.NormalizationVersion = request.NormalizationVersion;
        acquisition.AcquiredAt = request.AcquiredAt;
        acquisition.ObservedAt = request.ObservedAt;
        acquisition.State = request.State;
        acquisition.Detail = request.Detail;
        acquisition.FactsJson = request.FactsJson;
        acquisition.CapabilitiesJson = request.CapabilitiesJson;
        acquisition.ContentFingerprint = fingerprint;

        // ---- 2) Resolução das ENTIDADES pelos vínculos de ORIGEM ------------------------------------------
        // Um objeto observado em DOIS conjuntos é resolvido UMA vez: a chave é (tenant, namespace, id externo),
        // nunca o nome nem o UPN. É esta consulta única que faz duas observações apontarem para uma entidade.
        var externalIds = request.Sets
            .SelectMany(s => s.Objects)
            .Select(o => (o.ExternalId ?? "").Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var links = externalIds.Count == 0
            ? new List<IdentitySourceLink>()
            : await _db.IdentitySourceLinks
                .Where(l => l.DirectoryNamespace == ns && externalIds.Contains(l.ExternalId))
                .ToListAsync(ct);

        var linkByExternalId = links.ToDictionary(l => l.ExternalId, StringComparer.Ordinal);

        // Observações já gravadas para ESTA aquisição — o que torna o retry idempotente.
        var existingObservations = await _db.IdentityEntityObservations
            .Where(o => o.AcquisitionId == request.AcquisitionId)
            .ToListAsync(ct);
        var observationByKey = existingObservations
            .ToDictionary(o => (o.IdentityEntityId, o.Set));

        var setStateByKey = acquisition.Sets.ToDictionary(s => s.Set);

        // Uma MESMA entidade pode ser observada em vários conjuntos nesta aquisição; a projeção de estado
        // atual é aplicada UMA vez por entidade, com a última observação vencendo de forma determinística.
        var touched = new Dictionary<string, (IdentityEntity Entity, IdentityObservedObject Observed)>(StringComparer.Ordinal);

        foreach (var set in request.Sets)
        {
            var preserved = 0;

            foreach (var observed in set.Objects)
            {
                var externalId = (observed.ExternalId ?? "").Trim();
                if (externalId.Length == 0) continue;

                IdentityEntity entity;
                if (linkByExternalId.TryGetValue(externalId, out var link))
                {
                    // Vínculo existente: a MESMA entidade, mesmo que o nome tenha mudado desde a última coleta.
                    entity = await LoadEntityAsync(link.IdentityEntityId, ct);
                    link.ConnectorConfigId = request.Origin.ConnectorConfigId;
                    link.Provider = request.Origin.Provider;
                    if (request.AcquiredAt >= link.LastObservedAt)
                    {
                        link.LastObservedAt = request.AcquiredAt;
                        link.LastAcquisitionId = request.AcquisitionId;
                    }
                }
                else
                {
                    // Objeto novo NESTE namespace. Um identificador igual em outro namespace já produziu outra
                    // entidade — e continua produzindo, porque o namespace faz parte da chave.
                    entity = new IdentityEntity
                    {
                        Kind = observed.Kind,
                        DisplayName = observed.DisplayName,
                        UserPrincipalName = observed.UserPrincipalName,
                        FirstObservedAt = request.AcquiredAt,
                        LastObservedAt = request.AcquiredAt,
                        CurrentAsOf = request.AcquiredAt,
                        CurrentAcquisitionId = request.AcquisitionId,
                    };
                    _db.IdentityEntities.Add(entity);

                    link = new IdentitySourceLink
                    {
                        IdentityEntityId = entity.Id,
                        ConnectorConfigId = request.Origin.ConnectorConfigId,
                        Provider = request.Origin.Provider,
                        DirectoryNamespace = ns,
                        ExternalId = externalId,
                        FirstLinkedAt = request.AcquiredAt,
                        LastObservedAt = request.AcquiredAt,
                        LastAcquisitionId = request.AcquisitionId,
                    };
                    _db.IdentitySourceLinks.Add(link);
                    linkByExternalId[externalId] = link;
                }

                // ---- OBSERVAÇÃO: evidência daquela aquisição, escrita uma única vez -----------------------
                if (observationByKey.TryGetValue((entity.Id, set.Set), out var observation))
                {
                    // Retry da MESMA aquisição sobre o MESMO conjunto: a evidência já está registrada e não é
                    // reescrita. Reescrevê-la permitiria que um reprocessamento alterasse o passado.
                    preserved++;
                }
                else
                {
                    observation = new IdentityEntityObservation
                    {
                        AcquisitionId = request.AcquisitionId,
                        IdentityEntityId = entity.Id,
                        Set = set.Set,
                        ExternalId = externalId,
                        Kind = observed.Kind,
                        DisplayNameObserved = observed.DisplayName,
                        UserPrincipalNameObserved = observed.UserPrincipalName,
                        RolesObserved = observed.Roles?.ToList() ?? new List<string>(),
                        Detail = observed.Detail,
                        ObservedAt = request.AcquiredAt,
                    };
                    _db.IdentityEntityObservations.Add(observation);
                    observationByKey[(entity.Id, set.Set)] = observation;
                    preserved++;
                }

                touched[externalId] = (entity, observed);
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

        // ---- 3) PROJEÇÃO do estado atual, com ordenação determinística ------------------------------------
        foreach (var (entity, observed) in touched.Values)
        {
            if (request.AcquiredAt < entity.FirstObservedAt) entity.FirstObservedAt = request.AcquiredAt;
            if (request.AcquiredAt > entity.LastObservedAt) entity.LastObservedAt = request.AcquiredAt;

            // Uma coleta ATRASADA não substitui o estado atual mais recente. O empate é resolvido pelo
            // identificador da aquisição (ordem ordinal do GUID) — determinístico e estável entre execuções.
            var newer = request.AcquiredAt > entity.CurrentAsOf
                || (request.AcquiredAt == entity.CurrentAsOf
                    && request.AcquisitionId.CompareTo(entity.CurrentAcquisitionId) >= 0);
            if (!newer) continue;

            entity.Kind = observed.Kind;
            entity.DisplayName = observed.DisplayName;
            entity.UserPrincipalName = observed.UserPrincipalName;
            entity.CurrentAsOf = request.AcquiredAt;
            entity.CurrentAcquisitionId = request.AcquisitionId;
            entity.UpdatedAt = request.AcquiredAt;
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
    /// Carrega a entidade canônica alvo de um vínculo já existente. O vínculo é tenant-safe por FK composta,
    /// então a entidade tem de existir neste tenant: a ausência é corrupção relacional, não um caso normal.
    /// </summary>
    private async Task<IdentityEntity> LoadEntityAsync(Guid id, CancellationToken ct) =>
        await _db.IdentityEntities.FirstOrDefaultAsync(e => e.Id == id, ct)
        ?? throw new InvalidOperationException(
            $"Vínculo de origem aponta para a entidade de identidade {id}, que não existe neste tenant.");

    private static string? Join(string? a, string? b) =>
        string.Join(" ", new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } s ? s : null;

    /// <summary>
    /// Fingerprint determinístico do CONTEÚDO observado (fatos + capacidades + estado + conjuntos + objetos).
    /// Serve para RECONHECER que duas aquisições viram a mesma coisa; nunca para deduplicá-las — observar de
    /// novo é um fato novo, e apagá-lo destruiria a prova de que a coleta aconteceu.
    /// </summary>
    private static string Fingerprint(IdentityAcquisitionRequest request)
    {
        var sb = new StringBuilder();
        sb.Append(request.State).Append('|')
          .Append(request.Origin.DirectoryNamespace).Append('|')
          .Append(request.FactsJson).Append('|')
          .Append(request.CapabilitiesJson);

        foreach (var set in request.Sets.OrderBy(s => (int)s.Set))
        {
            sb.Append("|set:").Append((int)set.Set).Append(':').Append(set.Outcome).Append(':')
              .Append(set.ObservedCount).Append(':').Append(set.IsComplete);
            foreach (var o in set.Objects.OrderBy(o => o.ExternalId, StringComparer.Ordinal))
                sb.Append('#').Append(o.ExternalId).Append(':').Append((int)o.Kind);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
