using System;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-MVP-PRODUCT-03] Linguagem de GESTÃO dos achados — autoridade do relatório
// ============================================================================
// O corpo principal do relatório executivo é lido por quem decide orçamento e prioridade, não por quem
// opera o diretório. Ele precisa dizer o que o achado significa para o negócio e qual é o alcance — sem
// afirmar mais do que a coleta provou.
//
// Escopo DELIBERADAMENTE fechado: só os três achados com jornada de remediação nesta entrega têm redação
// executiva própria. Qualquer outro cai no texto factual já gravado na avaliação, que continua sendo
// exibido literalmente. Um dicionário aberto convidaria a inventar prosa para achados que ninguém revisou.
//
// Funções PURAS sobre campos JÁ CONGELADOS na fotografia (indicador, veredito, quantidade afetada): é o que
// garante que reexportar o mesmo relatório produza exatamente o mesmo texto, sem congelar prosa no banco.
//
// ⚠️ A tela tem a sua própria camada de apresentação (`knight.models.ts`), com propósito diferente: lá o
// texto é a SITUAÇÃO em uma linha, ao lado do número; aqui é o IMPACTO para a gestão. Os dois respeitam as
// mesmas ressalvas — registro de MFA não comprova imposição, teto de privilégio é parâmetro do AEGIS, e
// atividade desconhecida não comprova desuso — mas não são o mesmo texto e não se substituem.

/// <summary>Leitura executiva de UM achado congelado: título, impacto e alcance.</summary>
/// <param name="Title">Título claro do achado, no limite do que a regra observa.</param>
/// <param name="Impact">Por que isso importa para o negócio — sem exagero e sem conclusão não sustentada.</param>
/// <param name="Reach">O alcance observado (quantidade afetada), já em linguagem de gestão.</param>
/// <param name="Caveat">O que o achado explicitamente NÃO afirma. Nulo quando não há ressalva a fazer.</param>
public sealed record KnightFindingNarrative(string Title, string Impact, string Reach, string? Caveat);

/// <summary>Redação executiva determinística dos achados KNIGHT — usada pelo relatório publicado.</summary>
public static class KnightFindingNarratives
{
    /// <summary>
    /// Compõe a leitura executiva de um achado a partir do que está CONGELADO na fotografia. Para um achado
    /// sem redação própria, devolve o título do catálogo e a evidência gravada — nunca uma frase inventada.
    /// </summary>
    public static KnightFindingNarrative For(
        string indicatorId, string catalogTitle, KnightIndicatorStatus status, int affectedCount, string evidence)
    {
        var id = (indicatorId ?? "").Trim();
        var exposed = status is KnightIndicatorStatus.Exposed or KnightIndicatorStatus.Mitigated;
        var n = affectedCount;

        return id switch
        {
            "AK-ENTRA-001" => new KnightFindingNarrative(
                "Contas administrativas sem segundo fator registrado no diretório",
                "Uma conta administrativa que depende só de senha é o caminho mais curto entre um vazamento de " +
                "credencial e o controle do ambiente. O segundo fator é o que separa uma senha comprometida de " +
                "um incidente.",
                exposed
                    ? $"{n} conta(s) administrativa(s) sem nenhum método capaz de segundo fator no relatório de registro."
                    : "Nenhuma conta administrativa aparece sem método capaz de segundo fator no relatório de registro.",
                "Registro de método não comprova que a política exige o segundo fator no acesso. Verificar a " +
                "política de acesso condicional é um passo à parte desta constatação."),

            "AK-ENTRA-002" => new KnightFindingNarrative(
                "Objetos com papel administrativo sujeitos a revisão de acesso",
                "Cada objeto com papel administrativo amplia a superfície que um atacante pode aproveitar e o " +
                "número de caminhos que a auditoria precisa acompanhar. Manter o mínimo necessário reduz as duas coisas.",
                exposed
                    ? $"{n} objeto(s) com papel administrativo — pessoas, aplicações e grupos, juntos."
                    : "A quantidade de objetos com papel administrativo está dentro do teto parametrizado.",
                "Não é uma lista de acessos desnecessários: o AEGIS não sabe quem precisa de qual papel. O teto " +
                "usado na comparação é um parâmetro do AEGIS — o NIST recomenda menor privilégio, mas não fixa " +
                "um número. A decisão é da revisão humana."),

            "AK-ENTRA-004" => new KnightFindingNarrative(
                "Contas de convidado sem sinal de acesso na janela avaliada",
                "Acessos concedidos a terceiros continuam válidos até que alguém os reveja. Sem confirmação de " +
                "que ainda são necessários, eles permanecem como porta aberta sem dono definido.",
                exposed
                    ? $"{n} conta(s) de convidado sem sinal de acesso registrado dentro da janela da regra."
                    : "Nenhuma conta de convidado ficou sem sinal de acesso dentro da janela da regra.",
                "Atividade desconhecida não é inatividade comprovada: parte destes convidados pode simplesmente " +
                "não ter registro de acesso disponível. Confirmar com a área responsável se o acesso ainda é " +
                "necessário vem ANTES de desativar qualquer conta."),

            _ => new KnightFindingNarrative(
                string.IsNullOrWhiteSpace(catalogTitle) ? id : catalogTitle,
                string.IsNullOrWhiteSpace(evidence) ? "Sem evidência registrada nesta avaliação." : evidence,
                exposed ? $"{n} objeto(s) afetado(s) segundo a regra." : "Sem objetos afetados segundo a regra.",
                null),
        };
    }

    /// <summary>
    /// A primeira providência sugerida em linguagem de gestão. Para os convidados, a primeira ação é
    /// CONFIRMAR a necessidade do acesso — desativar antes disso trocaria um risco por uma interrupção de
    /// serviço, com base numa inatividade que a coleta não comprovou.
    /// </summary>
    public static string FirstAction(string indicatorId, string catalogRecommendation) =>
        (indicatorId ?? "").Trim() switch
        {
            "AK-ENTRA-001" =>
                "Registrar um método resistente a phishing para cada conta administrativa listada e, em seguida, " +
                "confirmar na política de acesso condicional que o segundo fator é exigido de fato.",
            "AK-ENTRA-002" =>
                "Conduzir uma revisão de acesso do conjunto listado, separando pessoas, aplicações e grupos, e " +
                "remover apenas os papéis que a área responsável confirmar como desnecessários.",
            "AK-ENTRA-004" =>
                "Confirmar com a área responsável, para cada convidado listado, se o acesso ainda é necessário. " +
                "Somente os acessos confirmados como desnecessários devem ser desativados.",
            _ => catalogRecommendation ?? "",
        };
}
