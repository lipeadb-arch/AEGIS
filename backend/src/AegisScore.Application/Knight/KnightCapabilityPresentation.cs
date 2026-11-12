using System;
using System.Text.RegularExpressions;

namespace AegisScore.Application.Knight;

/// <summary>
/// [AEGIS-KNIGHT-SCOPE-NAVIGATION-01] Texto de APRESENTAÇÃO de uma limitação de coleta. Só reescreve o que a pessoa lê: o detalhe gravado
/// pela coleta, o desfecho, a regra, a pontuação, a cobertura e as fotografias continuam como estão. O HTTP e o endpoint seguem nos
/// detalhes técnicos (diagnóstico).
/// </summary>
public static class KnightCapabilityPresentation
{
    // Detalhe gravado pelo coletor do Azure na leitura de certificados de UM cofre: "certificados do cofre {nome}: {motivo}".
    private static readonly Regex VaultCertificates = new(@"^certificados do cofre (?<name>.*?): ", RegexOptions.CultureInvariant);

    /// <summary>
    /// O Azure recusou (403) a leitura dos certificados de um cofre inventariado: a frase diz o que foi encontrado e o que não foi avaliado.
    /// Sem HTTP registrado (coletas antigas), vale a autorização recusada. Qualquer outro caso devolve o detalhe como está.
    /// </summary>
    public static string? Detail(KnightCapability capability, KnightCapabilityOutcome outcome, string? detail, int? httpStatus)
    {
        if (capability != KnightCapability.AzureKeyVaultCertificates || detail is null) return detail;
        var refused = httpStatus is { } h ? h == 403 : outcome == KnightCapabilityOutcome.InsufficientPermission;
        if (!refused) return detail;
        var m = VaultCertificates.Match(detail);
        if (!m.Success) return detail;
        var name = m.Groups["name"].Value.Trim();
        var vault = name.Length == 0 ? "um cofre" : $"o cofre {name}";
        return $"O AEGIS encontrou {vault}, mas o Azure recusou a leitura dos dados dos certificados. Por isso, a validade dos certificados não foi avaliada.";
    }

    public static string? Detail(KnightCapabilityStatus c) => Detail(c.Capability, c.Outcome, c.Detail, c.Diagnostic?.HttpStatus);

    /// <summary>A frase foi reescrita: o registro original vai para os detalhes técnicos.</summary>
    public static bool Rewritten(KnightCapabilityStatus c) => !string.Equals(Detail(c), c.Detail, StringComparison.Ordinal);
}
