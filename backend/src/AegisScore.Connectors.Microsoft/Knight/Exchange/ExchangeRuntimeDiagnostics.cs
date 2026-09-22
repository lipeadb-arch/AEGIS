using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace AegisScore.Connectors.Microsoft.Knight.Exchange;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-03] Verificação de RUNTIME do adaptador de coleta do Exchange Online, executável
/// dentro da imagem de implantação — <c>dotnet AegisScore.Api.dll --knight-exchange-runtime-check</c>.
///
/// <para>O desenho é o mesmo já validado no bloco do Microsoft Teams, e pelo mesmo motivo: o que precisa ser
/// provado não é que "existe um pwsh na imagem", é que <b>o caminho que o produto realmente usa funciona naquele
/// ambiente</b>. Chamar <c>pwsh -Command 'Import-Module …'</c> de fora não reproduz o adaptador — quem monta o
/// <c>PSModulePath</c> a partir da configuração é o leitor, não o shell.</para>
///
/// <para><b>O que esta verificação NÃO prova, e é dito na saída em vez de escondido:</b> os comandos de leitura
/// do Exchange (<c>Get-OrganizationConfig</c>, <c>Get-Mailbox</c> e os demais) não existem antes de uma conexão.
/// Eles são importados pela própria conexão com o locatário. Um gate offline não pode encontrá-los — e, se
/// pudesse, encontrá-los ainda não diria nada sobre a AUTORIZAÇÃO para executá-los. Por isso eles saem daqui
/// como CONTRATO DECLARADO, com essa ressalva explícita, e nunca como verificação aprovada. Confundir os dois
/// conjuntos produziria um gate verde para um ambiente em que nenhuma leitura funcionaria.</para>
/// </summary>
public static class ExchangeRuntimeDiagnostics
{
    /// <summary>Argumento de linha de comando que dispara a verificação em vez de subir a API.</summary>
    public const string Argument = "--knight-exchange-runtime-check";

    /// <summary>Marca que o CI procura na saída. Só é impressa quando TUDO passou.</summary>
    public const string SuccessMarker = "adaptador-exchange=OK";

    /// <summary>
    /// Cabeçalho do alcance declarado. O CI EXIGE esta linha: um gate que perde a própria ressalva passa a ser
    /// lido como prova do que não provou, e é exatamente assim que um verde offline vira promessa de coleta.
    /// </summary>
    public const string ScopeMarker = "alcance-do-gate: offline, sem locatário";

    /// <summary>Seção de configuração das opções do adaptador (a mesma que a API usa ao registrar o serviço).</summary>
    public const string ConfigurationSection = "Knight:Exchange";

    public static bool Requested(string[]? args) =>
        args is not null && args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Monta o leitor a partir da MESMA seção de configuração que a API usa ao registrar o adaptador e executa a
    /// verificação. Devolve 0 quando o runtime está provado, 1 em qualquer outro caso. Nenhuma falha é mascarada.
    /// </summary>
    public static async Task<int> RunFromConfigurationAsync(
        IConfiguration? exchangePowerShell, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        var options = new ExchangePowerShellOptions();
        exchangePowerShell?.Bind(options);

        output.WriteLine($"executável={options.Executable}");
        output.WriteLine($"caminho do módulo={options.ModulePath ?? "(padrão da máquina)"}");
        output.WriteLine($"teto de enumeração={options.EnumerationLimit}");

        return await RunAsync(new PowerShellExchangeAdminReader(options), output, ct);
    }

    /// <summary>Executa a verificação com um leitor já construído (o mesmo caminho, sem ler o ambiente).</summary>
    public static async Task<int> RunAsync(IExchangeAdminReader reader, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);

        ExchangeAdminOutput result;
        try
        {
            result = await reader.CheckRuntimeAsync(ct);
        }
        catch (ExchangeAdminTransportException ex)
        {
            output.WriteLine("FALHA no transporte do adaptador: " + ex.Message);
            return 1;
        }

        output.WriteLine($"PowerShell={result.Runtime.PowerShell ?? "?"} plataforma={result.Runtime.Platform ?? "?"}");
        output.WriteLine($"ExchangeOnlineManagement={result.Runtime.Module ?? "(módulo não importado)"}");

        if (string.IsNullOrWhiteSpace(result.Runtime.Module))
        {
            output.WriteLine(
                "FALHA: o módulo ExchangeOnlineManagement não foi importado no ambiente em que o adaptador roda. "
                + $"Categoria={result.ConnectionErrorCategory ?? "n/a"} Diagnóstico={result.ConnectionErrorId ?? "n/a"}");
            return 1;
        }

        var checks = result.Reads
            .Where(r => string.Equals(r.Capability, "ModuleCheck", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (checks.Count == 0)
        {
            output.WriteLine("FALHA: o adaptador não devolveu a verificação de comandos do módulo.");
            return 1;
        }

        var missing = checks.Where(r => !r.Ok).Select(r => r.Command).ToList();
        output.WriteLine($"comandos do módulo verificados={checks.Count} ausentes={missing.Count}");
        if (missing.Count > 0)
        {
            output.WriteLine("FALHA: comandos ausentes nesta versão do módulo: " + string.Join(", ", missing));
            return 1;
        }

        // O contrato dos comandos de SESSÃO é IMPRESSO, não verificado. A distinção é o ponto desta linha.
        var session = result.Reads
            .FirstOrDefault(r => string.Equals(r.Capability, "SessionCommandContract", StringComparison.OrdinalIgnoreCase));
        var sessionCount = session is null || session.Items.ValueKind != System.Text.Json.JsonValueKind.Array
            ? 0
            : session.Items.GetArrayLength();
        if (sessionCount == 0)
        {
            output.WriteLine("FALHA: o adaptador não declarou o contrato de comandos de sessão.");
            return 1;
        }
        output.WriteLine(
            $"comandos de sessão declarados={sessionCount} "
            + "(NÃO verificados aqui: só existem após a conexão com o locatário, e a existência deles não comprova autorização)");

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
    private const string SyntheticToken = "AEGIS-MARCADOR-SINTETICO-NAO-E-CREDENCIAL-4Xc7Rm2pW9tK6yB3";

    private const string SyntheticOrganization = "aegis-sintetico.example.invalid";

    /// <summary>
    /// Executa o script REAL no caminho de erro e verifica o contrato de saída.
    ///
    /// O cenário: uma coleta pedida com um marcador no lugar do token, num processo sem rede. A conexão não tem
    /// como acontecer — e é justamente esse o ponto. O que se exige do script é que, tendo falhado, ele ainda
    /// assim devolva um DOCUMENTO de resultado íntegro: com a categoria do erro, sem exceção vazando e sem
    /// nenhum dos marcadores na saída. Um erro dentro do próprio tratamento de erro apareceria aqui.
    /// </summary>
    private static async Task<int> RunSyntheticFailureAsync(
        IExchangeAdminReader reader, TextWriter output, CancellationToken ct)
    {
        ExchangeAdminOutput result;
        try
        {
            result = await reader.ReadAsync(new ExchangeAdminCredentials(SyntheticOrganization, SyntheticToken), ct);
        }
        catch (ExchangeAdminTransportException ex)
        {
            output.WriteLine("FALHA no cenário sintético: o adaptador não devolveu documento de resultado. " + ex.Message);
            return 1;
        }

        output.WriteLine(
            $"cenário sintético: conectado={result.Connected} categoria={result.ConnectionErrorCategory ?? "(nenhuma)"} "
            + $"diagnóstico={result.ConnectionErrorId ?? "(nenhum)"} leituras={result.Reads.Count} "
            + $"teto={result.EnumerationLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(não informado)"}");

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

        // O documento MÍNIMO existe como última linha de defesa. Se foi ELE que saiu, a montagem normal quebrou.
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

        // O ALCANCE do gate sai junto com o sucesso, e de propósito: um marcador verde lido sem esta lista vira
        // "o Exchange está validado nesta imagem", que é falso. O que foi provado aqui é pequeno e específico.
        output.WriteLine(ScopeMarker);
        output.WriteLine(
            "  PROVADO: o módulo fixado CARREGA nesta imagem; os comandos do módulo existem nesta versão; o "
            + "adaptador real executa o script no processo real; o token sintético não vaza para saída alguma.");
        output.WriteLine(
            "  NÃO PROVADO: compatibilidade integral do módulo com esta distribuição (a lista oficial de Linux "
            + "suportado é Ubuntu, e esta imagem é Debian); autenticação; aceitação do token pelo locatário; os "
            + "comandos de sessão, que só existem depois da conexão; e qualquer coleta real de configuração.");

        output.WriteLine(SuccessMarker);
        return 0;
    }
}
