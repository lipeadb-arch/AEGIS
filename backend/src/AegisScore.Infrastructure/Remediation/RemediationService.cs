using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Remediation;

/// <summary>
/// [AEGIS-MVP-PRODUCT-03] Implementação da jornada de remediação de um achado do AEGIS KNIGHT.
///
/// O que este serviço deliberadamente NÃO faz: não escreve <c>TenantControlState</c>, <c>EvidenceSignal</c>,
/// score, cobertura, veredito de indicador nem fotografia de postura. Concluir uma ação NÃO torna um achado
/// conforme — o diagnóstico continua sendo autoridade exclusiva da avaliação, e a ação apenas registra o que
/// a organização decidiu fazer a respeito.
///
/// Isolamento: o tenant é resolvido do contexto (claim) e aplicado pelo Global Query Filter fail-closed. Uma
/// avaliação, um achado ou uma ação de outro tenant são indistinguíveis de inexistentes.
///
/// Concorrência: DUAS defesas. A explícita compara a versão que o cliente leu com a vigente (o navegador que
/// ficou com a tela aberta recebe 409 em vez de sobrescrever); a do banco é o token de concorrência do EF na
/// mesma coluna, que pega a corrida entre duas requisições que passaram juntas pela primeira checagem.
/// </summary>
public sealed class RemediationService : IRemediationService
{
    /// <summary>Teto do título — o mesmo <c>HasMaxLength</c> da coluna; recusa antes de o banco truncar.</summary>
    private const int MaxTitleLength = 200;

    /// <summary>Teto dos textos longos (ação proposta, relato de execução, referência de evidência).</summary>
    private const int MaxTextLength = 2000;

    /// <summary>Teto dos campos curtos de pessoa/área.</summary>
    private const int MaxNameLength = 200;

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public RemediationService(AegisScoreDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    // ---- Criação -------------------------------------------------------------------------------------

    public async Task<ActionPlanView?> CreateForFindingAsync(
        CreateFindingActionPlanCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        EnsureTenant();

        var indicatorId = (command.IndicatorId ?? "").Trim();
        var title = Trim(command.Title, MaxTitleLength);
        if (string.IsNullOrWhiteSpace(title))
            throw new ActionPlanValidationException("O título da ação é obrigatório.");

        // O achado precisa existir NA AVALIAÇÃO indicada — não na mais recente. Vincular a ação ao achado de
        // hoje enquanto o usuário olha o resultado de ontem seria trocar a origem em silêncio. A FONTE e o
        // MODO da avaliação viajam junto: eles fazem parte da identidade do problema, não são decoração.
        var finding = await _db.KnightIndicatorResults.AsNoTracking()
            .Where(i => i.RunId == command.RunId && i.IndicatorId == indicatorId)
            .Join(_db.KnightAssessmentRuns.AsNoTracking(), i => i.RunId, r => r.Id,
                (i, r) => new { i.AffectedObjectCount, r.SourceType, r.Mode })
            .FirstOrDefaultAsync(ct);
        if (finding is null) return null;

        // Duplicidade por clique repetido: enquanto houver ação ATIVA para a mesma ORIGEM — tenant, fonte,
        // modo e achado —, abre-se a existente. Uma ação encerrada libera a origem: o problema pode
        // reaparecer e merecer novo ciclo.
        var active = await FindActiveAsync(indicatorId, finding.SourceType, finding.Mode, ct);
        if (active is not null)
            throw new ActionPlanConflictException(
                "Já existe uma ação ativa para este achado nesta fonte. Abra a ação existente em vez de criar outra.",
                active);

        var now = _clock.GetUtcNow();
        var plan = new ActionPlan
        {
            // Sem TenantId aqui — carimbado no SaveChangesAsync (fail-closed), como nas demais escritas.
            RiskId = null,                       // ação de ACHADO: nenhum risco fictício é criado para a FK
            Treatment = RiskTreatmentType.Mitigar,
            Title = title,
            Description = Trim(command.ProposedAction, MaxTextLength),
            ResponsiblePerson = Trim(command.ResponsiblePerson, MaxNameLength),
            ResponsibleArea = Trim(command.ResponsibleArea, MaxNameLength),
            DueDate = command.DueDate,
            Status = ActionPlanStatus.Aberto,
            KnightIndicatorId = indicatorId,
            OriginRunId = command.RunId,
            OriginSourceType = finding.SourceType,
            OriginMode = finding.Mode,
            OriginAffectedCount = finding.AffectedObjectCount,
            CycleStartedAt = now,
            Version = 1,
        };

        _db.ActionPlans.Add(plan);
        AddEvent(plan, actor, ActionPlanEventKind.Created, now, null, ActionPlanStatus.Aberto,
            $"Ação criada a partir do achado {indicatorId} da avaliação {command.RunId:D} " +
            $"({DescribeOrigin(finding.SourceType, finding.Mode)}).");
        await SaveWithConcurrencyGuardAsync(ct);

        return await GetAsync(plan.Id, ct);
    }

    // ---- Leitura -------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ActionPlanView>> ListAsync(
        ActionPlanFilter filter, CancellationToken ct = default)
    {
        EnsureTenant();

        // Somente ações de ACHADO. Os planos legados de tratamento de risco continuam pertencendo ao registro
        // de riscos e não são reapresentados aqui como se fossem remediações de identidade.
        var query = _db.ActionPlans.AsNoTracking()
            .Include(p => p.Validations)
            .Include(p => p.Events)
            .Where(p => p.KnightIndicatorId != null);

        var indicatorId = (filter.IndicatorId ?? "").Trim();
        if (indicatorId.Length > 0)
            query = query.Where(p => p.KnightIndicatorId == indicatorId);

        // Fonte e modo restringem a lista à MESMA procedência do que a tela está exibindo. Sem isso, a ação
        // criada sobre o cenário de demonstração apareceria na Central de Prioridades ao lado dos achados de
        // uma coleta real, com exatamente a mesma aparência de trabalho real em curso.
        if (filter.SourceType is { } sourceType)
            query = query.Where(p => p.OriginSourceType == sourceType);
        if (filter.Mode is { } mode)
            query = query.Where(p => p.OriginMode == mode);

        if (filter.ActiveOnly)
            query = query.Where(p =>
                p.Status == ActionPlanStatus.Aberto
                || p.Status == ActionPlanStatus.EmAndamento
                || p.Status == ActionPlanStatus.AguardandoValidacao);

        // Ordenação por instante feita no cliente: o SQLite dos testes não traduz ORDER BY de DateTimeOffset.
        // O conjunto por tenant é pequeno (ações abertas manualmente), então materializar é proporcional.
        var rows = await query.ToListAsync(ct);
        return rows
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Select(ToView).ToList();
    }

    public async Task<ActionPlanView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        EnsureTenant();
        var plan = await _db.ActionPlans.AsNoTracking()
            .Include(p => p.Validations)
            .Include(p => p.Events)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        return plan is null ? null : ToView(plan);
    }

    // ---- Mutações ------------------------------------------------------------------------------------

    public async Task<ActionPlanView?> UpdateAsync(
        Guid id, UpdateActionPlanCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        EnsureTenant();
        var plan = await LoadForWriteAsync(id, command.ExpectedVersion, ct);
        if (plan is null) return null;

        var now = _clock.GetUtcNow();
        var changes = new List<string>();

        if (command.Title is not null)
        {
            var t = Trim(command.Title, MaxTitleLength);
            if (string.IsNullOrWhiteSpace(t))
                throw new ActionPlanValidationException("O título da ação é obrigatório.");
            if (!string.Equals(plan.Title, t, StringComparison.Ordinal)) { plan.Title = t; changes.Add("título"); }
        }
        if (command.ProposedAction is not null)
        {
            var d = Trim(command.ProposedAction, MaxTextLength);
            if (!string.Equals(plan.Description, d, StringComparison.Ordinal)) { plan.Description = d; changes.Add("ação proposta"); }
        }
        if (command.ResponsiblePerson is not null)
        {
            var r = Trim(command.ResponsiblePerson, MaxNameLength);
            if (!string.Equals(plan.ResponsiblePerson, r, StringComparison.Ordinal)) { plan.ResponsiblePerson = r; changes.Add("responsável"); }
        }
        if (command.ResponsibleArea is not null)
        {
            var a = Trim(command.ResponsibleArea, MaxNameLength);
            if (!string.Equals(plan.ResponsibleArea, a, StringComparison.Ordinal)) { plan.ResponsibleArea = a; changes.Add("área"); }
        }
        if (command.DueDate is not null && plan.DueDate != command.DueDate)
        {
            plan.DueDate = command.DueDate;
            changes.Add("prazo");
        }

        if (changes.Count > 0)
            AddEvent(plan, actor, ActionPlanEventKind.Edited, now, null, null,
                "Campos alterados: " + string.Join(", ", changes) + ".");

        if (command.Status is { } target && target != plan.Status)
            ApplyTransition(plan, target, actor, now, note: null);

        if (changes.Count == 0 && command.Status is null)
            return ToView(plan);   // nada a fazer: não incrementa versão nem escreve trilha vazia

        plan.Version++;
        plan.UpdatedAt = now;
        await SaveWithConcurrencyGuardAsync(ct);
        return await GetAsync(plan.Id, ct);
    }

    public async Task<ActionPlanView?> RecordExecutionAsync(
        Guid id, RecordExecutionCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        EnsureTenant();
        var plan = await LoadForWriteAsync(id, command.ExpectedVersion, ct);
        if (plan is null) return null;

        var notes = Trim(command.Notes, MaxTextLength);
        if (string.IsNullOrWhiteSpace(notes))
            throw new ActionPlanValidationException(
                "Descreva o que foi feito. Um registro de execução sem descrição não serve para validar depois.");

        var now = _clock.GetUtcNow();
        plan.ExecutionNotes = notes;
        plan.ExecutionEvidenceRef = Trim(command.EvidenceReference, MaxTextLength);
        plan.ExecutedAt = now;

        AddEvent(plan, actor, ActionPlanEventKind.ExecutionRecorded, now, null, null,
            "Execução relatada. Relato não comprova correção — a validação é um ato à parte.");

        // Um NOVO relato de execução recomeça a comprovação: as validações anteriores passam a descrever um
        // trabalho que não é este. Elas continuam na trilha; apenas deixam de autorizar o encerramento — e a
        // base recalculada abaixo já reflete isso, porque a aplicabilidade é medida contra ExecutedAt.
        if (plan.Status != ActionPlanStatus.AguardandoValidacao)
            ApplyTransition(plan, ActionPlanStatus.AguardandoValidacao, actor, now,
                "Execução relatada — aguardando comprovação.");

        plan.Version++;
        plan.UpdatedAt = now;
        await SaveWithConcurrencyGuardAsync(ct);
        return await GetAsync(plan.Id, ct);
    }

    public async Task<ActionPlanView?> ValidateAsync(
        Guid id, ValidateActionPlanCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        EnsureTenant();
        var plan = await LoadForWriteAsync(id, command.ExpectedVersion, ct);
        if (plan is null) return null;

        var indicatorId = plan.KnightIndicatorId ?? "";
        if (indicatorId.Length == 0)
            throw new ActionPlanValidationException(
                "Esta ação não veio de um achado do KNIGHT; não há indicador a validar.");

        var now = _clock.GetUtcNow();
        ActionPlanValidation validation;

        if (command.ValidationRunId is { } runId)
        {
            // Comparação automática: o servidor decide o desfecho. O cliente não escolhe "resolvido".
            var origin = await LoadEvidenceAsync(plan.OriginRunId, indicatorId, ct);
            if (origin is null)
                throw new ActionPlanValidationException(
                    "A avaliação que originou esta ação não está mais disponível neste cliente; não há base de comparação.");

            var evidence = await LoadEvidenceAsync(runId, indicatorId, ct);
            if (evidence is null)
                throw new ActionPlanValidationException(
                    "A avaliação indicada como evidência não existe neste cliente.");

            // O relato de execução entra na conta: uma coleta ANTERIOR a ele pode mostrar mudança real no
            // ambiente, mas não mudança produzida por este trabalho.
            var verdict = KnightValidationEvaluator.Evaluate(indicatorId, origin, evidence, plan.ExecutedAt);

            validation = new ActionPlanValidation
            {
                ActionPlanId = plan.Id,
                IndicatorId = indicatorId,
                Method = ActionPlanValidationMethod.NewAssessment,
                Outcome = verdict.Outcome,
                ValidationRunId = runId,          // referência de EVIDÊNCIA — distinta da avaliação de origem
                EvidenceReference = null,
                EvidenceCollectedAt = evidence.CollectedAt,
                PrecedesReportedExecution = verdict.PrecedesReportedExecution,
                ObservedBefore = verdict.ObservedBefore,
                ObservedAfter = verdict.ObservedAfter,
                ObjectsNoLongerPresent = verdict.ObjectsNoLongerPresent,
                ComparedBySets = verdict.ComparedBySets,
                Rationale = verdict.Rationale,
                DecidedAt = now,
                DecidedByAccountId = actor.AccountId,
                DecidedByName = actor.DisplayName,
            };
        }
        else
        {
            // Atestação humana: exige evidência REFERENCIADA e é registrada como atestação, jamais como
            // comprovação técnica. Um "feito" sem referência não vira registro de validação.
            var reference = Trim(command.EvidenceReference, MaxTextLength);
            if (string.IsNullOrWhiteSpace(reference))
                throw new ActionPlanValidationException(
                    "Uma validação humana exige a referência da evidência (chamado, documento ou registro). " +
                    "Sem ela, é um comentário — não uma comprovação.");

            var note = Trim(command.Note, MaxTextLength);
            validation = new ActionPlanValidation
            {
                ActionPlanId = plan.Id,
                IndicatorId = indicatorId,
                Method = ActionPlanValidationMethod.HumanEvidence,
                Outcome = ActionPlanValidationOutcome.HumanAttested,
                ValidationRunId = null,
                EvidenceReference = reference,
                EvidenceCollectedAt = null,          // não há coleta: é a palavra de alguém, e é dito assim
                PrecedesReportedExecution = false,
                ObservedBefore = plan.OriginAffectedCount,
                ObservedAfter = null,
                ObjectsNoLongerPresent = null,
                ComparedBySets = false,
                Rationale = string.IsNullOrWhiteSpace(note)
                    ? "Atestação humana com evidência referenciada. Não é comprovação técnica: o AEGIS não " +
                      "verificou o ambiente para este registro."
                    : note + " — Atestação humana com evidência referenciada; o AEGIS não verificou o ambiente " +
                      "para este registro.",
                DecidedAt = now,
                DecidedByAccountId = actor.AccountId,
                DecidedByName = actor.DisplayName,
            };
        }

        _db.ActionPlanValidations.Add(validation);
        AddEvent(plan, actor, ActionPlanEventKind.ValidationRecorded, now, null, null,
            $"{RemediationReading.MethodLabel(validation.Method)} — {RemediationReading.OutcomeLabel(validation.Outcome)}.");

        // A validação NÃO conclui a ação por conta própria: encerrar é decisão de gestão, registrada como
        // transição explícita. O que ela faz é dar (ou negar) a base para essa decisão.
        //
        // E NÃO move a etapa. "Aguardando validação" quer dizer "execução relatada, aguardando comprovação";
        // levar para lá uma ação que ninguém executou FABRICARIA a execução — a tela passaria a afirmar um
        // trabalho que não foi descrito por pessoa alguma.

        plan.Version++;
        plan.UpdatedAt = now;
        await SaveWithConcurrencyGuardAsync(ct);
        return await GetAsync(plan.Id, ct);
    }

    // ---- Apoio ---------------------------------------------------------------------------------------

    private Guid EnsureTenant() => _tenant.TenantId
        ?? throw new TenantSecurityException("Operação de remediação sem tenant resolvido no contexto (fail-closed).");

    /// <summary>
    /// Ação ATIVA já existente para a mesma ORIGEM — tenant (implícito), fonte, modo e achado. O indicador
    /// sozinho NÃO identifica o problema: "AK-ENTRA-001 na demonstração" e "AK-ENTRA-001 na coleta real do
    /// diretório" são dois problemas, e tratá-los como um faria a ação de treinamento bloquear a ação real.
    /// </summary>
    private async Task<Guid?> FindActiveAsync(
        string indicatorId, KnightSourceType sourceType, KnightAssessmentMode mode, CancellationToken ct)
    {
        var existing = await _db.ActionPlans.AsNoTracking()
            .Where(p => p.KnightIndicatorId == indicatorId
                        && p.OriginSourceType == sourceType
                        && p.OriginMode == mode
                        && (p.Status == ActionPlanStatus.Aberto
                            || p.Status == ActionPlanStatus.EmAndamento
                            || p.Status == ActionPlanStatus.AguardandoValidacao))
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);
        return existing;
    }

    /// <summary>Descrição curta da procedência, para a trilha dizer de qual coleta a ação nasceu.</summary>
    private static string DescribeOrigin(KnightSourceType source, KnightAssessmentMode mode) =>
        mode == KnightAssessmentMode.Demo
            ? $"fonte {source}, cenário de DEMONSTRAÇÃO"
            : $"fonte {source}, coleta real";

    /// <summary>
    /// Carrega a ação para escrita e valida a versão que o cliente leu. Versão divergente = alguém escreveu
    /// enquanto a tela estava aberta: recusa (409) em vez de descartar o trabalho da outra pessoa.
    /// </summary>
    private async Task<ActionPlan?> LoadForWriteAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var plan = await _db.ActionPlans
            .Include(p => p.Validations)
            .Include(p => p.Events)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return null;

        if (plan.Version != expectedVersion)
            throw new ActionPlanConflictException(
                $"Esta ação foi alterada por outra pessoa (versão {plan.Version}, você enviou {expectedVersion}). " +
                "Recarregue para ver a versão atual antes de gravar.");
        return plan;
    }

    /// <summary>
    /// Converte em 409 as DUAS corridas que o banco decide, e só elas:
    ///
    ///   • o token de concorrência do EF perdido — alguém gravou entre a leitura e o UPDATE;
    ///   • a violação do índice único PARCIAL de ação ativa por achado — duas criações (ou uma criação e uma
    ///     reabertura) passaram juntas pela checagem em memória e o banco desempatou.
    ///
    /// A segunda é reconhecida pelo NOME do índice, não por "erro de chave duplicada" em geral. Traduzir
    /// qualquer 23505 em "já existe ação ativa" mentiria sobre qualquer outra unicidade violada e esconderia
    /// um defeito real atrás de uma mensagem de negócio plausível.
    /// </summary>
    private async Task SaveWithConcurrencyGuardAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ActionPlanConflictException(
                "Esta ação foi alterada por outra pessoa enquanto a sua gravação estava em curso. " +
                "Recarregue para ver a versão atual antes de gravar.");
        }
        catch (DbUpdateException ex) when (IsActiveFindingIndexViolation(ex))
        {
            throw new ActionPlanConflictException(
                "Outra pessoa acabou de abrir (ou reabrir) uma ação para este mesmo achado nesta fonte. " +
                "Recarregue a lista e trabalhe na ação existente.");
        }
    }

    /// <summary>
    /// A exceção é a violação do índice <c>UX_ActionPlans_ActiveByFinding</c>? A checagem olha o NOME da
    /// restrição no texto do erro do provedor — o suficiente para não confundir esta invariante com nenhuma
    /// outra, e sem acoplar a Infrastructure ao tipo de exceção do Npgsql.
    /// </summary>
    private static bool IsActiveFindingIndexViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e.Message.Contains(ActiveFindingIndexName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Nome do índice único parcial de ação ativa por achado — o mesmo declarado no DbContext.</summary>
    internal const string ActiveFindingIndexName = "UX_ActionPlans_ActiveByFinding";

    /// <summary>
    /// Aplica uma transição PERMITIDA e registra a mudança na trilha. A permissão é decidida contra o que o
    /// CICLO tem registrado, não contra a mera sequência de etapas: sem esse teste, dois cliques levariam
    /// qualquer ação de "aberta" a "concluída" sem que ninguém tivesse descrito o que foi feito nem
    /// apresentado evidência alguma — e o relatório sairia dizendo que o ciclo foi encerrado.
    ///
    /// Reabrir uma ação encerrada REPACTUA o ciclo: a partir daí, a comprovação do ciclo anterior não
    /// autoriza mais nada. Ela permanece inteira no histórico — reabrir não apaga o que aconteceu.
    /// </summary>
    private void ApplyTransition(
        ActionPlan plan, ActionPlanStatus target, RemediationActor actor, DateTimeOffset now, string? note)
    {
        var basis = RemediationReading.BasisFor(plan, plan.Validations);
        if (!RemediationReading.IsAllowedTransition(plan.Status, target, basis))
        {
            var reason = target == ActionPlanStatus.Concluido
                ? RemediationReading.ClosureBlockedReason(plan.Status, basis)
                : null;
            throw new ActionPlanValidationException(
                $"Transição não permitida: de '{RemediationReading.StatusLabel(plan.Status)}' para " +
                $"'{RemediationReading.StatusLabel(target)}'." +
                (reason is null
                    ? target == ActionPlanStatus.AguardandoValidacao
                        ? " Aguardar validação pressupõe execução relatada — registre o que foi feito em vez " +
                          "de apenas avançar a etapa."
                        : ""
                    : " " + reason));
        }

        var from = plan.Status;
        var reopening = from == ActionPlanStatus.Concluido && target != ActionPlanStatus.Concluido;

        plan.Status = target;
        plan.CompletedAt = target == ActionPlanStatus.Concluido ? now : null;
        if (reopening) plan.CycleStartedAt = now;

        AddEvent(plan, actor, ActionPlanEventKind.StatusChanged, now, from, target,
            reopening
                ? (note is null ? "" : note + " ") +
                  "Ação reaberta: novo ciclo. A validação do ciclo anterior permanece no histórico e deixa de " +
                  "autorizar o encerramento — é preciso executar e comprovar de novo."
                : note);
    }

    /// <summary>
    /// Acrescenta uma entrada à trilha registrando-a EXPLICITAMENTE no DbSet, e não pela coleção de navegação
    /// do plano.
    ///
    /// Não é estilo: o <see cref="Entity"/> já nasce com <c>Id</c> preenchido, e o EF, ao descobrir por
    /// DetectChanges uma entidade nova dentro da navegação de um agregado JÁ RASTREADO, decide o estado pela
    /// chave — com a chave preenchida, ele a marca como <c>Modified</c>. O guard de escrita multi-tenant então
    /// procura a linha correspondente no banco, não a encontra (ela nunca existiu) e recusa a gravação inteira,
    /// fail-closed. Registrar no DbSet fixa o estado <c>Added</c> antes de qualquer detecção.
    /// </summary>
    private void AddEvent(
        ActionPlan plan, RemediationActor actor, ActionPlanEventKind kind, DateTimeOffset at,
        ActionPlanStatus? from, ActionPlanStatus? to, string? note) =>
        _db.ActionPlanEvents.Add(new ActionPlanEvent
        {
            ActionPlanId = plan.Id,
            Kind = kind,
            At = at,
            ActorAccountId = actor.AccountId,
            ActorName = actor.DisplayName ?? "",
            FromStatus = from,
            ToStatus = to,
            Note = note,
        });

    /// <summary>
    /// Reúne os fatos de UMA avaliação para o indicador pedido. Tudo passa pelo query filter fail-closed: uma
    /// avaliação de outro tenant simplesmente não é encontrada.
    /// </summary>
    private async Task<KnightRunEvidence?> LoadEvidenceAsync(Guid? runId, string indicatorId, CancellationToken ct)
    {
        if (runId is not { } id) return null;

        var run = await _db.KnightAssessmentRuns.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.Id, r.SourceType, r.Mode, r.CatalogVersion, r.StartedAt, r.CompletedAt, r.CapabilitiesJson })
            .FirstOrDefaultAsync(ct);
        if (run is null) return null;

        var indicator = await _db.KnightIndicatorResults.AsNoTracking()
            .Where(i => i.RunId == id && i.IndicatorId == indicatorId)
            .Select(i => new
            {
                i.Id, i.Status, i.AffectedObjectCount, i.HasAffectedDetail, i.AffectedDetailComplete, i.CollectedAt,
            })
            .FirstOrDefaultAsync(ct);

        var collectedAt = indicator?.CollectedAt ?? run.CompletedAt ?? run.StartedAt;

        // O conjunto é utilizável para comparação em DUAS situações, e a segunda é fácil de esquecer:
        //   • a coleta preservou a lista INTEIRA que produziu a contagem; ou
        //   • o achado, dentro do escopo de detalhe, não sinalizou objeto algum — o conjunto VAZIO é a
        //     resposta completa, e é exatamente ele que permite dizer que os objetos da origem saíram.
        // Tratar o segundo caso como "sem detalhe" faria a melhora mais limpa possível (de N para zero) ser
        // a única que o produto não conseguiria sustentar pelos conjuntos.
        var emptySetIsComplete = indicator is { AffectedObjectCount: 0 }
            && KnightAffectedObjectScope.IsInScope(indicatorId)
            && KnightIndicatorEvidence.IsConclusiveVerdict(indicator.Status);

        var detailComplete = indicator is { HasAffectedDetail: true, AffectedDetailComplete: true }
            || emptySetIsComplete;

        var ids = Array.Empty<string>();
        if (indicator is { HasAffectedDetail: true, AffectedDetailComplete: true })
        {
            ids = await _db.KnightAffectedObjects.AsNoTracking()
                .Where(o => o.IndicatorResultId == indicator.Id)
                .Select(o => o.ExternalId)
                .ToArrayAsync(ct);
        }

        return new KnightRunEvidence(
            run.Id, run.SourceType, run.Mode, run.CatalogVersion, collectedAt,
            IndicatorFound: indicator is not null,
            Status: indicator?.Status ?? KnightIndicatorStatus.NotEvaluated,
            AffectedCount: indicator?.AffectedObjectCount ?? 0,
            DetailComplete: detailComplete,
            AffectedExternalIds: ids,
            Capabilities: KnightCapabilitiesJson.Deserialize(run.CapabilitiesJson));
    }

    private static string? Trim(string? value, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        return v.Length <= max ? v : v[..max];
    }

    /// <summary>
    /// Compõe a visão de leitura. Duas coisas que ela apresenta SEPARADAS de propósito: a validação mais
    /// recente (o histórico) e a validação APLICÁVEL ao ciclo atual (o que sustenta a decisão de hoje). Uma
    /// ação reaberta tem as duas, e são diferentes — colapsá-las faria a tela reciclar uma comprovação velha.
    /// </summary>
    private static ActionPlanView ToView(ActionPlan p)
    {
        var cycleStart = RemediationReading.CycleStartOf(p);
        var ordered = p.Validations
            .OrderByDescending(v => v.DecidedAt).ThenByDescending(v => v.Id)
            .ToList();

        var applicableEntity = ordered
            .FirstOrDefault(v => RemediationReading.IsApplicableToCurrentCycle(v, cycleStart, p.ExecutedAt));

        ActionPlanValidationView ToValidationView(ActionPlanValidation v) => new(
            v.Method, v.Outcome, v.ValidationRunId, v.EvidenceReference,
            v.EvidenceCollectedAt, v.PrecedesReportedExecution,
            AppliesToCurrentCycle: RemediationReading.IsApplicableToCurrentCycle(v, cycleStart, p.ExecutedAt),
            v.ObservedBefore, v.ObservedAfter, v.ObjectsNoLongerPresent, v.ComparedBySets,
            v.Rationale, v.DecidedAt, v.DecidedByName);

        var validations = ordered.Select(ToValidationView).ToList();
        var applicable = applicableEntity is null ? null : ToValidationView(applicableEntity);

        var basis = new RemediationReading.ActionPlanCycleBasis(
            HasExecutionInCurrentCycle: p.ExecutedAt is { } e && e >= cycleStart,
            ApplicableOutcome: applicableEntity?.Outcome);

        var events = p.Events
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => new ActionPlanEventView(e.Kind, e.At, e.ActorName, e.FromStatus, e.ToStatus, e.Note))
            .ToList();

        return new ActionPlanView(
            p.Id,
            p.KnightIndicatorId,
            p.OriginRunId,
            p.OriginAffectedCount,
            p.OriginSourceType,
            p.OriginMode,
            p.Title ?? "",
            p.Description,
            p.ResponsiblePerson,
            p.ResponsibleArea,
            p.DueDate,
            p.Status,
            p.IsOverdue,
            p.IsActive,
            RemediationReading.NextStep(
                p.Status, p.IsOverdue, applicableEntity?.Outcome, ordered.FirstOrDefault()?.Outcome),
            p.ExecutionNotes,
            p.ExecutionEvidenceRef,
            p.ExecutedAt,
            p.CompletedAt,
            p.CreatedAt,
            cycleStart,
            p.Version,
            validations.FirstOrDefault(),
            applicable,
            RemediationReading.AllowedTransitions(p.Status, basis),
            RemediationReading.ClosureBlockedReason(p.Status, basis),
            validations,
            events);
    }
}
