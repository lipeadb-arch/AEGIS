using AegisScore.Application.Abstractions;
using AegisScore.Application.Advisories;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Advisories;

/// <summary>
/// Implementa o caso de uso "gerar advisory" sobre o AegisScoreDbContext + o motor de IA.
///
/// Fluxo: pede o rascunho à IA (Stub canned em DEV, LLM real com Ai:ApiKey), materializa a entidade
/// <see cref="RemediationAdvisory"/> e a persiste. O TenantId NÃO é atribuído aqui — o
/// <c>StampTenant</c> do DbContext o carimba no SaveChanges (fail-closed) a partir do tenant ambiente
/// resolvido do JWT; um tenant jamais grava advisory no escopo de outro. O texto vem SEMPRE do motor,
/// nunca do corpo do request (o cliente só escolhe o código do controle).
/// </summary>
public sealed class GenerateAdvisoryHandler : IGenerateAdvisoryHandler
{
    private readonly AegisScoreDbContext _db;
    private readonly IAiAssessmentService _ai;

    public GenerateAdvisoryHandler(AegisScoreDbContext db, IAiAssessmentService ai)
    {
        _db = db;
        _ai = ai;
    }

    public async Task<RemediationAdvisoryDto> HandleAsync(GenerateAdvisoryCommand command, CancellationToken ct = default)
    {
        var code = (command.SubcategoryCode ?? "").Trim();

        var draft = await _ai.GenerateAdvisoryAsync(new AdvisoryGenerationRequest(code), ct);

        var advisory = new RemediationAdvisory
        {
            SubcategoryCode = code,
            Title = draft.Title,
            DocumentedRisk = draft.DocumentedRisk,
            TechnicalSteps = draft.TechnicalSteps,
        };

        _db.RemediationAdvisories.Add(advisory);
        await _db.SaveChangesAsync(ct);   // StampTenant carimba o TenantId (fail-closed) aqui.

        return new RemediationAdvisoryDto(
            advisory.Id, advisory.SubcategoryCode, advisory.Title,
            advisory.DocumentedRisk, advisory.TechnicalSteps, advisory.CreatedAt);
    }
}
