using System;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Advisories;

/// <summary>
/// Comando de criação de uma Recomendação de Remediação para uma subcategoria NIST. Só o código do
/// controle trafega: o texto (título, risco, passo a passo) é REDIGIDO pelo motor de IA no handler —
/// o cliente não injeta prosa. O tenant NÃO é parâmetro (Zero Trust): resolvido do claim do JWT e
/// carimbado no SaveChanges (fail-closed).
/// </summary>
public record GenerateAdvisoryCommand(string SubcategoryCode);

/// <summary>
/// Contrato de leitura de um advisory na fronteira da API. O frontend jamais recebe a entidade de
/// domínio crua (<c>RemediationAdvisory</c>), o que nos deixa evoluir o modelo sem quebrar o cliente.
/// </summary>
/// <param name="SubcategoryCode">Código NIST-alvo ("PR.AA-01").</param>
/// <param name="DocumentedRisk">Risco Documentado — o "porquê" gerado pela IA.</param>
/// <param name="TechnicalSteps">Passo a Passo Técnico exportável — o "como fazer".</param>
public record RemediationAdvisoryDto(
    Guid Id,
    string SubcategoryCode,
    string Title,
    string DocumentedRisk,
    string TechnicalSteps,
    DateTimeOffset CreatedAt);

/// <summary>
/// Porta do caso de uso "gerar advisory". O CONTRATO vive na Application (que não conhece EF Core); a
/// implementação sobre o AegisScoreDbContext + IAiAssessmentService mora na Infrastructure — mesmo
/// padrão porta/adaptador das consultas de leitura (<c>IControlStateDashboardQuery</c> et al.),
/// espelhado aqui para o lado de ESCRITA.
/// </summary>
public interface IGenerateAdvisoryHandler
{
    /// <summary>Redige o texto (IA), persiste o advisory sob o tenant ambiente e devolve o DTO criado.</summary>
    Task<RemediationAdvisoryDto> HandleAsync(GenerateAdvisoryCommand command, CancellationToken ct = default);
}
