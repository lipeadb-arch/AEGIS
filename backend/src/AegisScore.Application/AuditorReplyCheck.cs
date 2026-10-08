using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AegisScore.Application.Abstractions;

/// <summary>Resposta do Auditor depois da conferência no servidor: texto, fontes citadas (existentes no contexto) e notas da conferência.</summary>
public sealed record AuditorCheckedReply(string Message, IReadOnlyList<AuditorContextSource> Sources, IReadOnlyList<string> Notes);

/// <summary>
/// [AEGIS-AUDITOR-CONTEXT-01] Conferência da resposta do Auditor contra as fontes do contexto montado — escritor puro (sem banco):
/// <list type="bullet">
/// <item>só valem citações a chaves deste contexto; citação inexistente sai do texto e é dita;</item>
/// <item>links no texto são removidos (as fontes já levam aos registros dentro da aplicação);</item>
/// <item>resposta que não cita nenhum registro do tenant (só referência ou nada) é marcada como orientação geral.</item>
/// </list>
/// </summary>
public static class AuditorReplyCheck
{
    public const int MaxMessage = 8000;

    private static readonly Regex Citation = new(@"\[([A-Z]\d{1,3})\]", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"\b(?:https?|ftp|file)://\S+|\bwww\.[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static AuditorCheckedReply Check(AuditorReply reply, AuditorAssessmentContext? context)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var notes = new List<string>();
        var registry = (context?.Sources ?? Array.Empty<AuditorContextSource>()).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var message = (reply.Message ?? "").Trim();

        var inText = Citation.Matches(message).Select(m => m.Groups[1].Value);
        var declared = (reply.CitedKeys ?? Array.Empty<string>()).Select(k => (k ?? "").Trim().ToUpperInvariant());
        var keys = inText.Concat(declared).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var unknown = keys.Where(k => !registry.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
        {
            message = Citation.Replace(message, m => registry.ContainsKey(m.Groups[1].Value) ? m.Value : "");
            notes.Add($"{unknown.Count} citação(ões) a fontes que não existem neste contexto foram removidas.");
        }

        var urls = Url.Matches(message).Count;
        if (urls > 0)
        {
            message = Url.Replace(message, "[link removido]");
            notes.Add($"{urls} link(s) no texto foram removidos: abra os registros pelas fontes citadas.");
        }

        if (message.Length > MaxMessage)
        {
            message = message[..MaxMessage].TrimEnd() + "…";
            notes.Add("A resposta foi encurtada no limite de tamanho.");
        }

        var cited = keys.Where(registry.ContainsKey).Select(k => registry[k]).ToList();
        if (registry.Count == 0)
            notes.Add("Não há registros do tenant no contexto desta resposta: trate-a como orientação geral.");
        else if (!cited.Any(s => AuditorSourceNature.SupportsEnvironmentClaim(s.Nature)))
            notes.Add("A resposta não citou registros do tenant: trate-a como orientação geral, não como constatação sobre o ambiente.");

        return new AuditorCheckedReply(message.Length == 0 ? "Não foi possível obter uma resposta aproveitável. Reformule a pergunta." : message, cited, notes);
    }
}
