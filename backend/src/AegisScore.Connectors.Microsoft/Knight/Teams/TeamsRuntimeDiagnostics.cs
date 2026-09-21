using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace AegisScore.Connectors.Microsoft.Knight.Teams;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] Verificação de RUNTIME do adaptador de coleta do Microsoft Teams, executável dentro
/// da imagem de implantação — <c>dotnet AegisScore.Api.dll --knight-teams-runtime-check</c>.
///
/// Por que existe, em vez de um comando solto de PowerShell no CI: o que precisa ser provado não é que "existe um
/// pwsh na imagem", é que <b>o caminho que o produto realmente usa funciona naquele ambiente</b>. Chamar
/// <c>pwsh -Command 'Import-Module MicrosoftTeams'</c> de fora NÃO reproduz o adaptador: o módulo fica num
/// diretório próprio da imagem e quem monta o <c>PSModulePath</c> é o <see cref="PowerShellTeamsAdminReader"/>, a
/// partir de <c>Knight:Teams:ModulePath</c>. Um teste que não reproduz essa configuração testa outra coisa — e foi
/// exatamente assim que a validação anterior falhou, com o módulo presente na imagem e ausente para o comando.
///
/// O que esta verificação exercita, de ponta a ponta:
///   • as opções do adaptador lidas da configuração REAL do ambiente (as variáveis <c>Knight__Teams__*</c>);
///   • a materialização do script EMBUTIDO no assembly, com as permissões que o produto aplica;
///   • o processo de PowerShell iniciado com o executável configurado e o <c>PSModulePath</c> que o produto monta;
///   • a entrega do pedido pela ENTRADA PADRÃO e a leitura da resposta pelo analisador do produto;
///   • a importação OFFLINE do módulo fixado e a existência de cada comando que a coleta executa;
///   • [Revisão dirigida] a EXECUÇÃO REAL do script no caminho de ERRO, com um cenário sintético: uma tentativa de
///     conexão com credenciais que são marcadores inventados. Procurar a palavra “catch” no arquivo não demonstra
///     comportamento algum; aqui o script roda e precisa devolver um documento de resultado legível, com categoria
///     e sem nenhum dos marcadores. É isto que prova, no runtime da imagem, que o contrato de saída se sustenta
///     quando algo dá errado — e que a montagem desse documento não quebra na conversão da lista de leituras.
///
/// O que ela NÃO faz: não autentica em locatário algum (os marcadores não autenticam em lugar nenhum e o passo roda
/// SEM REDE), não pede credencial e não lê configuração de cliente. É validação de ambiente — não é homologação.
/// </summary>
public static class TeamsRuntimeDiagnostics
{
    /// <summary>Argumento de linha de comando que dispara a verificação em vez de subir a API.</summary>
    public const string Argument = "--knight-teams-runtime-check";

    /// <summary>Marca que o CI procura na saída. Só é impressa quando TUDO passou.</summary>
    public const string SuccessMarker = "adaptador=OK";

    /// <summary>Seção de configuração das opções do adaptador (a mesma que a API usa ao registrar o serviço).</summary>
    public const string ConfigurationSection = "Knight:Teams";

    public static bool Requested(string[]? args) =>
        args is not null && args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Monta o leitor a partir da MESMA seção de configuração que a API usa ao registrar o adaptador e executa a
    /// verificação. Devolve o código de saída do processo: 0 quando o runtime está provado, 1 em qualquer outro
    /// caso. Nenhuma falha é mascarada.
    /// </summary>
    public static async Task<int> RunFromConfigurationAsync(
        IConfiguration? teamsPowerShell, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        var options = new TeamsPowerShellOptions();
        teamsPowerShell?.Bind(options);

        output.WriteLine($"executável={options.Executable}");
        output.WriteLine($"caminho do módulo={options.ModulePath ?? "(padrão da máquina)"}");

        return await RunAsync(new PowerShellTeamsAdminReader(options), output, ct);
    }

    /// <summary>Executa a verificação com um leitor já construído (o mesmo caminho, sem ler o ambiente).</summary>
    public static async Task<int> RunAsync(ITeamsAdminReader reader, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);

        TeamsAdminOutput result;
        try
        {
            result = await reader.CheckRuntimeAsync(ct);
        }
        catch (TeamsAdminTransportException ex)
        {
            output.WriteLine("FALHA no transporte do adaptador: " + ex.Message);
            return 1;
        }

        output.WriteLine($"PowerShell={result.Runtime.PowerShell ?? "?"} plataforma={result.Runtime.Platform ?? "?"}");
        output.WriteLine($"MicrosoftTeams={result.Runtime.Module ?? "(módulo não importado)"}");

        if (string.IsNullOrWhiteSpace(result.Runtime.Module))
        {
            output.WriteLine(
                "FALHA: o módulo MicrosoftTeams não foi importado no ambiente em que o adaptador roda. "
                + $"Categoria={result.ConnectionErrorCategory ?? "n/a"} Diagnóstico={result.ConnectionErrorId ?? "n/a"}");
            return 1;
        }

        var checks = result.Reads.Where(r => string.Equals(r.Capability, "ModuleCheck", StringComparison.OrdinalIgnoreCase)).ToList();
        if (checks.Count == 0)
        {
            output.WriteLine("FALHA: o adaptador não devolveu a verificação de comandos.");
            return 1;
        }

        var missing = checks.Where(r => !r.Ok).Select(r => r.Command).ToList();
        output.WriteLine($"comandos verificados={checks.Count} ausentes={missing.Count}");
        if (missing.Count > 0)
        {
            output.WriteLine("FALHA: comandos ausentes nesta versão do módulo: " + string.Join(", ", missing));
            return 1;
        }

        // A conexão NÃO é estabelecida nesta verificação — e isso é parte do que se prova aqui.
        if (result.Connected)
        {
            output.WriteLine("FALHA: a verificação de runtime não pode estabelecer conexão com locatário algum.");
            return 1;
        }

        output.WriteLine("module-check=OK");

        return await RunSyntheticFailureAsync(reader, output, ct);
    }

    /// <summary>Marcadores SINTÉTICOS. Não são credencial de nada: existem para serem procurados na saída.</summary>
    private const string SyntheticToken = "AEGIS-MARCADOR-SINTETICO-NAO-E-CREDENCIAL-7Q2bT9kM4wL1vR8n";

    /// <summary>
    /// [Revisão dirigida] Executa o script REAL no caminho de erro e verifica o contrato de saída.
    ///
    /// O cenário: uma coleta pedida com dois marcadores no lugar dos tokens, num processo sem rede. A conexão não
    /// tem como acontecer — e é justamente esse o ponto. O que se exige do script é que, tendo falhado, ele ainda
    /// assim devolva um DOCUMENTO de resultado íntegro: com a categoria do erro, sem exceção vazando e sem
    /// nenhum dos marcadores na saída. Um erro dentro do próprio tratamento de erro apareceria aqui.
    /// </summary>
    private static async Task<int> RunSyntheticFailureAsync(
        ITeamsAdminReader reader, TextWriter output, CancellationToken ct)
    {
        TeamsAdminOutput result;
        try
        {
            result = await reader.ReadAsync(
                new TeamsAdminCredentials("00000000-0000-0000-0000-000000000000", SyntheticToken, SyntheticToken), ct);
        }
        catch (TeamsAdminTransportException ex)
        {
            // Sem documento: o script quebrou o contrato de saída no caminho de erro.
            output.WriteLine("FALHA no cenário sintético: o adaptador não devolveu documento de resultado. " + ex.Message);
            return 1;
        }

        output.WriteLine(
            $"cenário sintético: conectado={result.Connected} categoria={result.ConnectionErrorCategory ?? "(nenhuma)"} "
            + $"diagnóstico={result.ConnectionErrorId ?? "(nenhum)"} leituras={result.Reads.Count}");

        if (result.Connected)
        {
            output.WriteLine("FALHA: o cenário sintético não pode resultar em conexão estabelecida.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(result.ConnectionErrorCategory))
        {
            output.WriteLine("FALHA: o script não classificou a falha — sem categoria, o operador não sabe o que houve.");
            return 1;
        }

        // O documento MÍNIMO existe como última linha de defesa. Se foi ELE que saiu, a montagem normal quebrou:
        // é exatamente o defeito da conversão da lista de leituras, e não pode passar por sucesso.
        if (string.Equals(result.ConnectionErrorId, "DocumentoDeResultadoIndisponivel", StringComparison.Ordinal))
        {
            output.WriteLine(
                "FALHA: o script caiu no documento mínimo de emergência — a montagem do resultado quebrou no "
                + "caminho de erro. A coleta não funcionaria neste runtime.");
            return 1;
        }

        var saida = string.Join(" ", new[]
        {
            result.ConnectionErrorCategory, result.ConnectionErrorId,
            string.Join(" ", result.Reads.Select(r => r.Command + " " + r.ErrorCategory + " " + r.ErrorId)),
        });
        if (saida.Contains(SyntheticToken, StringComparison.Ordinal))
        {
            output.WriteLine("FALHA: o marcador sintético de segredo apareceu na saída do adaptador.");
            return 1;
        }

        output.WriteLine("coleta-sintetica=OK");
        output.WriteLine(SuccessMarker);
        return 0;
    }
}
