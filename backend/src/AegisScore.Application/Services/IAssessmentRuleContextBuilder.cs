using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Services;

/// <summary>
/// "RAG por chave" do Aegis: recupera a <c>AegisAssessmentRule</c> de uma subcategoria e a formata num
/// fragmento de texto legível para o LLM — as "Regras do Jogo" (métricas de avaliação, lógica de cálculo e
/// fontes de evidência esperadas) que ancoram o veredito no NIST SP 800-53 Rev 5.2.0 em vez de no palpite
/// do modelo.
///
/// NÃO é busca vetorial: a recuperação é o lookup pela FK <c>subcategoryCode → AegisAssessmentRule</c>
/// (não há embedding store no projeto, e inventar um seria dependência desnecessária). A regra é reference
/// data GLOBAL (sem tenant). Devolve <c>null</c> quando a subcategoria não tem regra extraída (ex.: as 9
/// lacunas de proveniência de GOVERN), e o avaliador degrada limpo para a definição pura da subcategoria.
/// </summary>
public interface IAssessmentRuleContextBuilder
{
    /// <summary>
    /// Monta o fragmento de prompt com a regra de avaliação da subcategoria, ou <c>null</c> se não existir.
    /// </summary>
    Task<string?> BuildAsync(string subcategoryCode, CancellationToken ct = default);
}
