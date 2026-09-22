using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AegisScore.Connectors.Microsoft.Knight.PowerShell;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02/03] Sanitização do DIAGNÓSTICO que sai de um processo de coleta em PowerShell.
///
/// Por que existe: truncar NÃO é sanitizar. Um texto de erro escrito por um módulo de terceiro em erro-padrão
/// pode repetir o cabeçalho <c>Authorization</c>, o token que originou a chamada ou o corpo bruto da resposta;
/// cortar os primeiros 500 caracteres pode preservar exatamente o pedaço perigoso. Aqui o texto é REESCRITO
/// antes de chegar a qualquer log — e nada deste texto vai para o ADM, para a API nem para o relatório: o que
/// o cliente lê é montado pelo coletor a partir da CATEGORIA e do COMANDO.
///
/// Duas camadas, de propósito:
///   1. os segredos CONHECIDOS desta coleta (os tokens que o próprio AEGIS acabou de entregar ao processo)
///      são removidos por igualdade — é a garantia mais forte, e não depende de a forma do segredo ser adivinhada;
///   2. as formas GENÉRICAS de segredo (JWT, cabeçalho de autorização, atribuições do tipo "token=…", e qualquer
///      sequência longa e opaca) são removidas por padrão — cobre o que o AEGIS não entregou e não conhece.
///
/// Nasceu no adaptador do Microsoft Teams e serve agora também ao do Exchange Online: a regra de sanitização
/// é a mesma para qualquer processo externo, e manter uma cópia por adaptador significaria corrigir um defeito
/// de redação em dois lugares — foi exatamente o tipo de duplicação que a revisão do bloco anterior custou caro.
/// </summary>
internal static class PowerShellDiagnosticScrubber
{
    /// <summary>Marcador que substitui tudo o que foi removido. Curto e reconhecível no log.</summary>
    internal const string Marker = "[REDIGIDO]";

    /// <summary>Tamanho máximo do diagnóstico registrado, aplicado DEPOIS da sanitização.</summary>
    private const int MaxLength = 500;

    /// <summary>Caracteres aceitos num identificador técnico de erro (o resto é descartado).</summary>
    private static readonly Regex IdentifierNoise = new(@"[^A-Za-z0-9._/:-]", RegexOptions.Compiled);

    private static readonly Regex[] SecretShapes =
    [
        // JWT / token de acesso: três segmentos base64url separados por ponto, começando por "eyJ".
        new(@"eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]*", RegexOptions.Compiled),

        // Cabeçalho de autorização, em qualquer grafia, com ou sem esquema.
        new(@"(?i)\bauthorization\b\s*[:=]\s*\S+", RegexOptions.Compiled),
        new(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled),

        // Atribuições nomeadas de segredo: token=…, client_secret: …, password=…, api-key …
        new(@"(?i)\b(client[_-]?secret|secret|password|senha|api[_-]?key|access[_-]?token|refresh[_-]?token|token|pwd)\b\s*[:=]\s*\S+",
            RegexOptions.Compiled),

        // Última rede: qualquer sequência longa e opaca de caracteres de codificação. Um identificador de erro
        // legítimo é curto e tem separadores; um segredo é justamente uma tira longa e contínua.
        new(@"[A-Za-z0-9+/=_-]{32,}", RegexOptions.Compiled),
    ];

    /// <summary>
    /// Sanitiza um texto de diagnóstico. <paramref name="knownSecrets"/> são os valores que o AEGIS entregou ao
    /// processo nesta coleta — removidos por igualdade, antes de qualquer heurística.
    /// </summary>
    internal static string Scrub(string? text, IEnumerable<string?>? knownSecrets = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var value = text;

        if (knownSecrets is not null)
        {
            foreach (var secret in knownSecrets)
            {
                // Segredo curto demais não é removido por igualdade: removê-lo destruiria o texto inteiro sem
                // proteger nada (as formas genéricas abaixo continuam valendo).
                if (secret is null || secret.Length < 8) continue;
                value = value.Replace(secret, Marker, StringComparison.Ordinal);
            }
        }

        foreach (var shape in SecretShapes)
            value = shape.Replace(value, Marker);

        value = Whitespace.Replace(value, " ").Trim();
        return value.Length > MaxLength ? value[..MaxLength] + "…" : value;
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Sanitiza um IDENTIFICADOR técnico de erro vindo do adaptador. O script já o restringe na origem; isto é a
    /// segunda barreira, para o caso de a saída vir de outro lugar que não o script embutido.
    ///
    /// A regra da "tira longa e opaca" é aplicada por SEGMENTO, e não ao identificador inteiro: um identificador
    /// legítimo é uma composição de palavras curtas separadas por ponto, barra ou traço
    /// (<c>TooManyRequests/HttpRequestException</c> tem 36 caracteres e não é segredo nenhum), enquanto um
    /// segredo é uma sequência CONTÍNUA de caracteres de codificação. Aplicar a regra ao todo redigiria
    /// diagnóstico útil sem proteger nada a mais.
    /// </summary>
    internal static string? ScrubIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var id = IdentifierNoise.Replace(value.Trim(), "");
        if (id.Length > 120) id = id[..120];
        if (id.Length == 0) return null;

        if (Jwt.IsMatch(id)) return Marker;
        foreach (var segment in id.Split(IdentifierSeparators, StringSplitOptions.RemoveEmptyEntries))
            if (IsOpaque(segment))
                return Marker;

        return id;
    }

    private static readonly char[] IdentifierSeparators = ['/', '.', '-', ':', '_'];

    /// <summary>
    /// Um SEGMENTO contínuo longo o bastante para ser um segredo codificado — e que não seja uma PALAVRA.
    ///
    /// [Revisão dirigida] Só o comprimento não serve como critério: nomes técnicos legítimos passam de 24
    /// caracteres com folga (UnauthorizedAccessException tem 27), e redigi-los apaga justamente o diagnóstico que
    /// o operador precisa — sanitizar não pode custar a causa. Um segredo codificado, por outro lado, praticamente
    /// nunca é só letras em CamelCase: base64 e hexadecimal trazem dígitos, “+”, “=” ou uma caixa uniforme.
    ///
    /// Por isso o segmento é redigido quando é longo E NÃO tem a forma de nome composto (letras, com maiúsculas e
    /// minúsculas misturadas). O critério é conservador nos dois sentidos: uma cadeia longa só de dígitos, só de
    /// maiúsculas ou com sinais de codificação continua saindo como <see cref="Marker"/>.
    /// </summary>
    private static bool IsOpaque(string segment) =>
        segment.Length >= 24 && !NameLikeSegment.IsMatch(segment);

    /// <summary>Nome composto: só letras, com pelo menos uma maiúscula e pelo menos uma minúscula.</summary>
    private static readonly Regex NameLikeSegment = new(
        @"^(?=.*[a-z])(?=.*[A-Z])[A-Za-z]+$", RegexOptions.Compiled);

    private static readonly Regex Jwt = new(
        @"eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]*", RegexOptions.Compiled);
}
