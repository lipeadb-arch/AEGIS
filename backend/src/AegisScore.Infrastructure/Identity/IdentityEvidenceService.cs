using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Identity;

/// <summary>
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Implementação da Evidence Fabric de identidade: o ÚNICO ponto que faz a
/// aquisição real do Microsoft Entra ID (reusando o coletor do KNIGHT e o transporte/credencial existentes) e
/// persiste o snapshot NORMALIZADO, tenant-safe, com proveniência e completude. Tanto o assessment do AEGIS
/// KNIGHT quanto a rota de postura NIST convergem para cá — UMA aquisição por operação lógica, sem um segundo
/// cliente Graph, credencial ou consulta duplicada.
///
/// Degradação segura: uma coleta que FALHE não apaga nem falsifica o último snapshot válido — só atualiza o
/// desfecho da última tentativa e a saúde do conector. Isolamento por tenant garantido pelo Global Query
/// Filter (leitura) + stamping no SaveChanges (escrita) + FK composta (Id, TenantId) no banco. NUNCA persiste
/// nome, e-mail, ID de usuário, aplicação, token, segredo ou payload — só agregados tipados.
/// </summary>
public sealed class IdentityEvidenceService : IIdentityEvidenceService
{
    /// <summary>
    /// Versão CORRENTE do schema do snapshot de evidência de identidade. O v2
    /// ([AEGIS-MVP-MICROSOFT-COVERAGE-03]) acrescenta ao <c>FactsJson</c> os agregados de risco de identidade e
    /// de métodos de autenticação, envelopando as observações do v1 em vez de substituí-las.
    /// </summary>
    public const string SchemaVersion = IdentityEvidenceFactsJson.CurrentSchemaVersion;

    /// <summary>Schema ANTERIOR — snapshots gravados assim continuam sendo lidos sem migration nem reescrita.</summary>
    public const string LegacySchemaVersion = IdentityEvidenceFactsJson.LegacySchemaVersion;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly AegisScoreDbContext _db;
    private readonly IKnightCollectorRegistry _registry;
    private readonly IKnightSourceConfigurationProvider _config;
    private readonly IIdentityAcquisitionStore _acquisitions;
    private readonly ITenantContext _tenant;
    private readonly ILogger<IdentityEvidenceService>? _log;

    public IdentityEvidenceService(
        AegisScoreDbContext db,
        IKnightCollectorRegistry registry,
        IKnightSourceConfigurationProvider config,
        IIdentityAcquisitionStore acquisitions,
        ITenantContext tenant,
        ILogger<IdentityEvidenceService>? log = null)
    {
        _db = db;
        _registry = registry;
        _config = config;
        _acquisitions = acquisitions;
        _tenant = tenant;
        _log = log;
    }

    public async Task<IdentityEvidenceAcquisition> CollectAsync(CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException("Aquisição de evidência de identidade sem tenant resolvido no contexto (fail-closed).");

        // O conector é a autoridade da fonte + o alvo da FK tenant-safe + onde a saúde/última sync é registrada.
        //
        // SEM RASTREAMENTO de propósito. Esta leitura acontece ANTES da coleta e ANTES da seção crítica, e
        // serve apenas para decidir se há o que coletar. Se ela ficasse rastreada, a instância carregada aqui
        // seria devolvida mais tarde, dentro da transação, com os valores de AGORA — e a comparação temporal
        // da saúde do conector estaria sendo feita contra um estado já vencido, que é exatamente a corrida
        // que a seção crítica existe para eliminar. Lá dentro o conector é lido de novo, para atualização.
        var connector = await _db.Connectors.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Provider == ConnectorProvider.Microsoft && c.Capability == ConnectorCapability.IdentityPosture, ct);

        if (connector is null)
            return new IdentityEvidenceAcquisition(IdentityEvidenceConnectorState.NotConfigured, null, null);

        // Recusa conector desabilitado/desconectado — não coleta e NÃO destrói a última evidência preservada.
        if (!connector.Enabled)
            return new IdentityEvidenceAcquisition(IdentityEvidenceConnectorState.Disabled, null, await LoadViewAsync(connector.Id, ct));

        // Valida a presença de material de autenticação (segredo decifrável e completo) sem devolvê-lo nunca.
        var configuration = await _config.ResolveAsync(tenantId, KnightSourceType.MicrosoftEntraId, ct);
        if (configuration is not KnightEntraIdConfiguration entra)
            return new IdentityEvidenceAcquisition(IdentityEvidenceConnectorState.MissingCredential, null, await LoadViewAsync(connector.Id, ct));

        // [AEGIS-ADM-01] O NAMESPACE do diretório vem da configuração EFETIVAMENTE resolvida — é ele que
        // delimita o espaço de identificadores dos objetos observados. Sem ele não é possível dizer a QUE
        // diretório um identificador pertence, e dois diretórios distintos acabariam unificados. Uma
        // configuração sem esse dado é material de autenticação incompleto, e a coleta nem começa.
        var directoryNamespace = (entra.AzureTenantId ?? "").Trim();
        if (directoryNamespace.Length == 0)
            return new IdentityEvidenceAcquisition(IdentityEvidenceConnectorState.MissingCredential, null, await LoadViewAsync(connector.Id, ct));

        // UMA aquisição real (o coletor do KNIGHT normaliza em fatos tipados; NUNCA cai para dados sintéticos).
        var collector = _registry.Resolve(KnightSourceType.MicrosoftEntraId);
        var result = await collector.CollectAsync(new KnightCollectionContext(tenantId, configuration), ct);

        // [AEGIS-ADM-01] A coleta vira uma AQUISIÇÃO canônica. O identificador é gerado UMA vez e sobrevive às
        // tentativas de gravação: uma corrida de unicidade reaplica a MESMA aquisição, sem uma segunda consulta
        // ao Graph — o que preserva "uma aquisição lógica = uma coleta".
        var origin = new IdentityAcquisitionOrigin(
            connector.Id, KnightSourceType.MicrosoftEntraId, directoryNamespace, result.SourceLabel);

        // HORÁRIO DE AQUISIÇÃO = quando o AEGIS concluiu ESTA coleta. <c>CollectedAt</c> é carimbado pelo
        // coletor com o NOSSO relógio ao terminar de ler o diretório; não é um "as of" da Microsoft — o Graph
        // não devolve nenhum. Por isso o horário OBSERVADO da aquisição permanece nulo (ver a fronteira de
        // compatibilidade): copiar um no outro atribuiria ao fornecedor uma informação que ele não deu.
        // É este instante, e só ele, que ordena as projeções de estado atual.
        var acquiredAt = result.CollectedAt == default ? DateTimeOffset.UtcNow : result.CollectedAt;
        var request = IdentityKnightBoundary.ToAcquisition(Guid.NewGuid(), origin, result, acquiredAt);

        var (record, view) = await PersistAsync(connector.Id, request, result, ct);

        // O consumidor recebe o resultado RECONSTRUÍDO a partir da aquisição PERSISTIDA — não o objeto
        // transitório que saiu do coletor. É o que faz "a avaliação leu a evidência gravada" ser verificável.
        return new IdentityEvidenceAcquisition(
            IdentityEvidenceConnectorState.Configured,
            IdentityKnightBoundary.ToCollectionResult(record),
            view,
            record.AcquisitionId);
    }

    public async Task<IdentityEvidenceProjection> GetLatestProjectionAsync(CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException("Leitura de evidência de identidade sem tenant resolvido no contexto (fail-closed).");

        var connector = await _db.Connectors.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Provider == ConnectorProvider.Microsoft && c.Capability == ConnectorCapability.IdentityPosture, ct);

        if (connector is null)
            return IdentityEvidenceProjection.Build(IdentityEvidenceConnectorState.NotConfigured, null);

        var view = await LoadViewAsync(connector.Id, ct);

        IdentityEvidenceConnectorState state;
        if (!connector.Enabled)
            state = IdentityEvidenceConnectorState.Disabled;
        else
        {
            var configuration = await _config.ResolveAsync(tenantId, KnightSourceType.MicrosoftEntraId, ct);
            state = configuration is KnightEntraIdConfiguration
                ? IdentityEvidenceConnectorState.Configured
                : IdentityEvidenceConnectorState.MissingCredential;
        }

        return IdentityEvidenceProjection.Build(state, view);
    }

    // ---- Persistência degradation-safe ----------------------------------------------------------

    /// <summary>
    /// [AEGIS-ADM-01] Grava a AQUISIÇÃO do ADM e o snapshot agregado no MESMO <c>SaveChanges</c>, e só então
    /// relê a aquisição do banco. A atomicidade não é zelo: se o snapshot fosse gravado e a aquisição não,
    /// existiria uma avaliação aparentemente sustentada por um registro inexistente.
    ///
    /// Uma corrida de unicidade (duas coletas simultâneas criando o MESMO vínculo de origem) é recuperada
    /// reaplicando a MESMA aquisição sobre o estado recarregado — sem uma segunda consulta ao Graph, porque a
    /// coleta já aconteceu e seu resultado está em memória.
    /// </summary>
    private async Task<(IdentityAcquisitionRecord Record, IdentityEvidenceSnapshotView View)> PersistAsync(
        Guid connectorId, IdentityAcquisitionRequest request, KnightCollectionResult result, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                // A SEÇÃO CRÍTICA começa ANTES da primeira leitura que informa uma decisão. Todas as
                // comparações de recência abaixo (entidade, vínculo, snapshot, saúde) são LER → DECIDIR →
                // GRAVAR: sem a trava, duas transações leem a MESMA versão antiga, ambas se julgam mais
                // recentes e a última a gravar vence — sem deadlock e sem violação de unicidade para
                // denunciar o erro.
                await _acquisitions.LockOriginAsync(request.Origin, ct);

                // Leitura DENTRO da seção crítica. Nada lido antes dela pode informar a decisão de recência.
                var connector = await _db.Connectors.FirstAsync(c => c.Id == connectorId, ct);
                await _db.Entry(connector).ReloadAsync(ct);

                await _acquisitions.PrepareAsync(request, ct);
                var view = await StageSnapshotAsync(connector, result, ct);
                await _db.SaveChangesAsync(ct);

                var record = await _acquisitions.ReadAsync(request.AcquisitionId, ct)
                    ?? throw new InvalidOperationException(
                        $"A aquisição de identidade {request.AcquisitionId} não pôde ser relida após a gravação.");

                await tx.CommitAsync(ct);
                return (record, view);
            }
            catch (Exception ex) when (attempt < MaxAttempts - 1 && IsRecoverableWriteRace(ex))
            {
                // Outra coleta tocou as MESMAS linhas entre a resolução e a gravação. A transação já foi
                // desfeita pelo descarte; reaplicar é seguro porque a aquisição mantém o MESMO identificador
                // e o MESMO conteúdo — é o retry idempotente que o modelo já prevê, e não uma segunda coleta.
                DetachWriteScope();
                _log?.LogInformation(
                    "Corrida de gravação na aquisição de identidade {AcquisitionId}; reaplicando sobre o estado recarregado.",
                    request.AcquisitionId);
            }
        }

        throw new InvalidOperationException(
            "A gravação da aquisição de identidade não se recuperou da corrida de escrita concorrente.");
    }

    private const int MaxAttempts = 4;

    /// <summary>
    /// Desfaz o rastreamento APENAS do que esta operação tocou, para que a reaplicação leia estado realmente
    /// recarregado. Deliberadamente não é <c>ChangeTracker.Clear()</c>: o contexto é COMPARTILHADO com quem
    /// chamou, e descartar tudo apagaria alterações alheias pendentes — um efeito colateral silencioso muito
    /// pior do que a corrida que estamos tratando.
    /// </summary>
    private void DetachWriteScope()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is IdentityAcquisition or IdentityEntity or IdentitySourceLink
                or IdentityEntityObservation or IdentityObservationSetState or IdentityEvidenceSnapshot
                or ConnectorConfig)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Corrida ESPERADA entre duas gravações concorrentes do mesmo diretório, em duas formas:
    ///   • violação da chave NATURAL do ADM — outra coleta criou o vínculo/observação primeiro;
    ///   • DEADLOCK ou falha de serialização — o PostgreSQL abortou uma das transações para desempatar.
    ///
    /// Nos dois casos a resposta certa é reaplicar a MESMA aquisição sobre o estado recarregado, e não
    /// desistir: a operação é idempotente por construção. Qualquer outra falha de gravação SOBE — mascarar
    /// erro como sucesso é exatamente o que produziria evidência silenciosamente incompleta.
    /// </summary>
    private static bool IsRecoverableWriteRace(Exception exception)
    {
        // O provedor embrulha falhas transitórias; a causa real pode estar a mais de um nível de profundidade.
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is PostgresException pg)
                return pg.SqlState is PostgresErrorCodes.DeadlockDetected
                        or PostgresErrorCodes.SerializationFailure
                    || (pg.SqlState == PostgresErrorCodes.UniqueViolation
                        && pg.ConstraintName is "UX_IdentitySourceLink_Natural"
                            or "UX_IdentityEntityObservation_Natural"
                            or "UX_IdentityObservationSetState_Natural"
                            // O snapshot agregado entra no MESMO SaveChanges, e a chave natural dele
                            // (tenant, conector) também pode ser disputada por duas coletas simultâneas.
                            or "UX_IdentityEvidenceSnapshot_Natural"
                            // Retry CONCORRENTE do MESMO identificador de aquisição: as duas tentativas
                            // inserem a mesma linha. Reaplicar encontra a aquisição já gravada e converge
                            // pelo caminho idempotente — jamais a reclassifica como aquisição nova.
                            or "PK_IdentityAcquisitions");

            if (ex.GetType().Name == "SqliteException"
                && ex.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                && (ex.Message.Contains("IdentitySourceLink", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("IdentityEntityObservation", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("IdentityObservationSetState", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("IdentityEvidenceSnapshot", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Prepara o snapshot agregado da Evidence Fabric SEM salvar (o <c>SaveChanges</c> é do chamador, junto com
    /// a aquisição). Duas garantias diferentes convivem aqui:
    ///
    ///   • DEGRADAÇÃO SEGURA — uma coleta que falhe registra a tentativa e preserva intactos os dados, o
    ///     instante e a completude da última evidência válida.
    ///   • SEM REGRESSÃO TEMPORAL — uma coleta ATRASADA (concluída depois, mas observada antes) não substitui
    ///     dados mais recentes, não se apresenta como "última tentativa" e não rebaixa a saúde do conector.
    ///     Ela continua sendo gravada como AQUISIÇÃO — evidência histórica não se perde —, mas não redefine o
    ///     presente. As três projeções têm relógios próprios porque significam coisas diferentes: último dado
    ///     VÁLIDO, última TENTATIVA e saúde da integração.
    ///
    /// A comparação só é confiável porque a leitura acontece dentro da seção crítica aberta em
    /// <c>PersistAsync</c>: fora dela, duas transações leriam a mesma versão antiga e a ordem de gravação
    /// decidiria o vencedor.
    /// </summary>
    private async Task<IdentityEvidenceSnapshotView> StageSnapshotAsync(
        ConnectorConfig connector, KnightCollectionResult result, CancellationToken ct)
    {
        var producedData = result.State is KnightSourceState.Completed or KnightSourceState.PartialCollection;

        // Instante em que o AEGIS concluiu a COLETA (relógio nosso, carimbado pelo coletor ao terminar). Não é
        // horário do fornecedor: o Graph não devolve um "as of", e inventá-lo a partir daqui atribuiria à
        // Microsoft uma informação que ela não deu. É este instante que ordena as projeções.
        var now = result.CollectedAt == default ? DateTimeOffset.UtcNow : result.CollectedAt;

        // Envelope v2: observações ORDENADAS (fingerprint estável) + agregados do pacote de risco. Escrito
        // como OBJETO — a leitura reconhece o array nu do v1 pela forma da raiz.
        var factsJson = IdentityEvidenceFactsJson.Serialize(
            result.Facts.All, result.IdentityRisk, result.AuthenticationPosture);
        var capsJson = JsonSerializer.Serialize(result.Capabilities, Json);
        var fingerprint = Fingerprint(factsJson, capsJson, result.State);

        var snapshot = await _db.IdentityEvidenceSnapshots
            .FirstOrDefaultAsync(s => s.ConnectorConfigId == connector.Id, ct);

        var hadPriorData = snapshot?.LastCollectionAt is not null;

        if (snapshot is null)
        {
            snapshot = new IdentityEvidenceSnapshot
            {
                ConnectorConfigId = connector.Id,
                Source = result.SourceLabel,
                SourceType = result.Source,
                SchemaVersion = SchemaVersion,
                LastAttemptState = result.State,
                LastAttemptAt = now,
                LastAttemptDetail = result.Detail,
                DataState = KnightSourceState.NotConfigured,   // placeholder até haver dado
            };
            if (producedData)
            {
                snapshot.DataState = result.State;
                snapshot.LastCollectionAt = now;
                snapshot.FactsJson = factsJson;
                snapshot.CapabilitiesJson = capsJson;
                snapshot.Fingerprint = fingerprint;
            }
            _db.IdentityEvidenceSnapshots.Add(snapshot);
        }
        else
        {
            // ÚLTIMA TENTATIVA (é onde a degradação aparece). Só é reescrita por uma tentativa que não seja
            // anterior à registrada: uma coleta atrasada não é "a última tentativa" só por ter terminado
            // depois, e apresentá-la como tal esconderia a tentativa mais recente de quem vai agir.
            if (now >= snapshot.LastAttemptAt)
            {
                snapshot.LastAttemptState = result.State;
                snapshot.LastAttemptAt = now;
                snapshot.LastAttemptDetail = result.Detail;
                snapshot.Source = result.SourceLabel;
                snapshot.SchemaVersion = SchemaVersion;
            }

            // ÚLTIMO DADO VÁLIDO. Relógio PRÓPRIO: um sucesso atrasado não substitui um sucesso mais recente,
            // mesmo que a tentativa mais recente tenha falhado — são perguntas diferentes.
            if (producedData && (snapshot.LastCollectionAt is null || now >= snapshot.LastCollectionAt))
            {
                snapshot.DataState = result.State;
                snapshot.LastCollectionAt = now;
                if (!string.Equals(snapshot.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    snapshot.FactsJson = factsJson;
                    snapshot.CapabilitiesJson = capsJson;
                    snapshot.Fingerprint = fingerprint;
                }
                snapshot.UpdatedAt = now;
            }
            // FALHA (ou coleta atrasada): preserva DataState/Facts/Capabilities/LastCollectionAt — a última
            // evidência válida continua sendo a última evidência válida.
        }

        // SAÚDE da integração. Mesmo critério temporal: uma tentativa antiga concluindo tarde não rebaixa
        // (nem promove) o estado do conector. Falha total com evidência anterior = Degraded (ainda servimos a
        // última evidência válida); falha total sem evidência = Failed.
        if (connector.LastSyncAt is null || now >= connector.LastSyncAt)
        {
            connector.LastSyncAt = now;
            connector.LastStatus = result.State switch
            {
                KnightSourceState.Completed => ConnectorStatus.Healthy,
                KnightSourceState.PartialCollection => ConnectorStatus.Degraded,
                _ => hadPriorData ? ConnectorStatus.Degraded : ConnectorStatus.Failed,
            };
        }

        return ToView(connector.TenantId, snapshot);
    }

    private async Task<IdentityEvidenceSnapshotView?> LoadViewAsync(Guid connectorId, CancellationToken ct)
    {
        var snapshot = await _db.IdentityEvidenceSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ConnectorConfigId == connectorId, ct);
        return snapshot is null ? null : ToView(snapshot.TenantId, snapshot);
    }

    private IdentityEvidenceSnapshotView ToView(Guid tenantId, IdentityEvidenceSnapshot s)
    {
        // UMA desserialização do envelope por leitura — as observações e os agregados v2 saem da MESMA
        // fotografia; um snapshot v1 devolve observações e agregados nulos (jamais zeros inventados).
        var facts = DeserializeFacts(s.FactsJson);

        return new IdentityEvidenceSnapshotView(
            tenantId,
            s.ConnectorConfigId,
            s.SourceType,
            string.IsNullOrWhiteSpace(s.Source) ? "Microsoft Entra ID" : s.Source,
            s.SchemaVersion,
            s.DataState,
            s.LastAttemptState,
            s.LastCollectionAt,
            s.LastAttemptAt,
            s.LastAttemptDetail,
            facts.Observations,
            DeserializeCapabilities(s.CapabilitiesJson),
            facts.IdentityRisk,
            facts.AuthenticationPosture);
    }

    /// <summary>
    /// Lê o <c>FactsJson</c> em QUALQUER das versões do schema, delegando à AUTORIDADE ÚNICA compartilhada com
    /// a aquisição do ADM (<see cref="IdentityEvidenceFactsJson"/>). Snapshot e aquisição guardam o MESMO
    /// envelope: duas desserializações independentes divergiriam no primeiro ajuste de nomenclatura, e a
    /// divergência apareceria como fato ausente — ou seja, cobertura perdida sem causa real.
    /// </summary>
    internal IdentityEvidenceFacts DeserializeFacts(string? json) =>
        IdentityEvidenceFactsJson.Deserialize(json);

    private IReadOnlyList<KnightCapabilityStatus> DeserializeCapabilities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<KnightCapabilityStatus>();
        try
        {
            return JsonSerializer.Deserialize<List<KnightCapabilityStatus>>(json, Json) ?? new List<KnightCapabilityStatus>();
        }
        catch (JsonException ex)
        {
            _log?.LogWarning(ex, "CapabilitiesJson do snapshot de identidade ilegível; retornando sem capacidades.");
            return Array.Empty<KnightCapabilityStatus>();
        }
    }

    private static string Fingerprint(string factsJson, string capsJson, KnightSourceState state)
    {
        var bytes = Encoding.UTF8.GetBytes($"{state}|{factsJson}|{capsJson}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
