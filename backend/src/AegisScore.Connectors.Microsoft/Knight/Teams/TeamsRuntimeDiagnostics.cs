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
///   • a importação OFFLINE do módulo fixado e a existência de cada comando que a coleta executa.
///
/// O que ela NÃO faz: não autentica, não conecta em locatário nenhum, não pede credencial e não lê configuração de
/// cliente algum. É validação de ambiente — não é homologação.
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

        output.WriteLine(SuccessMarker);
        return 0;
    }
}
