using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using Microsoft.Extensions.Configuration;

namespace AegisScore.Connectors.Microsoft.Knight.Protection;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Verificação de RUNTIME do adaptador de proteção (Defender para Office 365 e Purview),
/// executável dentro da imagem de implantação — <c>dotnet AegisScore.Api.dll --knight-protection-runtime-check</c>.
///
/// <para>O mesmo desenho já firmado para o Teams e o Exchange Online: provar que o CAMINHO que o produto usa funciona
/// naquele ambiente — o script embutido materializado, o processo real do PowerShell, o módulo fixado importado pelo
/// PSModulePath que o adaptador monta — e não apenas que existe um <c>pwsh</c> na imagem.</para>
///
/// <para><b>O que NÃO se prova aqui, e a saída diz:</b> os comandos de leitura do Defender e do Purview só existem
/// depois da conexão com o locatário (são importados pelas sessões do Exchange Online e do Security &amp; Compliance);
/// a aceitação do token pelo locatário; e qualquer coleta real. O cenário sintético roda os DOIS perfis sem rede e
/// exige que, tendo falhado, o script devolva um documento de resultado íntegro, sem vazar o marcador de token.</para>
/// </summary>
public static class ProtectionRuntimeDiagnostics
{
    public const string Argument = "--knight-protection-runtime-check";

    /// <summary>Marca que o CI procura na saída. Só é impressa quando TUDO passou.</summary>
    public const string SuccessMarker = "adaptador-protecao=OK";

    /// <summary>Cabeçalho do alcance declarado — o CI exige esta linha.</summary>
    public const string ScopeMarker = "alcance-do-gate: offline, sem locatário";

    /// <summary>O adaptador de proteção usa o MESMO runtime do Exchange Online (mesma seção de configuração).</summary>
    public const string ConfigurationSection = ExchangeRuntimeDiagnostics.ConfigurationSection;

    /// <summary>Marcadores SINTÉTICOS — não são credencial de nada; existem para serem procurados na saída.</summary>
    internal const string SyntheticToken = "AEGIS-MARCADOR-SINTETICO-NAO-E-CREDENCIAL-Pr0t3c4o-EXO";
    internal const string SyntheticComplianceToken = "AEGIS-MARCADOR-SINTETICO-NAO-E-CREDENCIAL-Pr0t3c4o-IPPS";
    private const string SyntheticOrganization = "aegis-sintetico.example.invalid";

    public static bool Requested(string[]? args) =>
        args is not null && args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunFromConfigurationAsync(IConfiguration? exchangePowerShell, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var exo = new ExchangePowerShellOptions();
        exchangePowerShell?.Bind(exo);
        var options = new ProtectionPowerShellOptions
        {
            Executable = exo.Executable, ModulePath = exo.ModulePath, Timeout = exo.Timeout,
            MaxOutputBytes = exo.MaxOutputBytes, EnumerationLimit = exo.EnumerationLimit,
        };
        output.WriteLine($"executável={options.Executable}");
        output.WriteLine($"caminho do módulo={options.ModulePath ?? "(padrão da máquina)"}");
        var reader = new PowerShellProtectionAdminReader(options);
        return await RunAsync(reader, ct2 => reader.CheckRuntimeAsync(ct2), output, ct);
    }

    /// <summary>Executa a verificação com um leitor já construído (o mesmo caminho, sem ler o ambiente).</summary>
    public static async Task<int> RunAsync(
        IProtectionAdminReader reader, Func<CancellationToken, Task<ProtectionAdminOutput>> moduleCheck, TextWriter output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);

        ProtectionAdminOutput check;
        try
        {
            check = await moduleCheck(ct);
        }
        catch (ExchangeAdminTransportException ex)
        {
            output.WriteLine("FALHA no transporte do adaptador: " + ex.Message);
            return 1;
        }

        var runtime = check.Exchange.Runtime;
        output.WriteLine($"PowerShell={runtime.PowerShell ?? "?"} plataforma={runtime.Platform ?? "?"}");
        output.WriteLine($"ExchangeOnlineManagement={runtime.Module ?? "(módulo não importado)"}");
        if (string.IsNullOrWhiteSpace(runtime.Module))
        {
            output.WriteLine("FALHA: o módulo ExchangeOnlineManagement não foi importado no ambiente em que o adaptador roda.");
            return 1;
        }

        var checks = check.Exchange.Reads.Where(r => string.Equals(r.Capability, "ModuleCheck", StringComparison.OrdinalIgnoreCase)).ToList();
        var missing = checks.Where(r => !r.Ok).Select(r => r.Command).ToList();
        var needed = new[] { "Connect-ExchangeOnline", "Connect-IPPSSession", "Disconnect-ExchangeOnline" };
        var absent = needed.Where(n => !checks.Any(c => c.Ok && string.Equals(c.Command, n, StringComparison.OrdinalIgnoreCase))).ToList();
        output.WriteLine($"comandos de conexão verificados={checks.Count} ausentes={absent.Count}");
        if (checks.Count == 0 || missing.Count > 0 || absent.Count > 0)
        {
            output.WriteLine("FALHA: comandos de conexão ausentes nesta versão do módulo: " + string.Join(", ", absent.Concat(missing).Distinct()));
            return 1;
        }
        output.WriteLine("module-check=OK");

        foreach (var profile in new[] { ProtectionAdminRequest.Defender, ProtectionAdminRequest.Purview })
        {
            var purview = profile == ProtectionAdminRequest.Purview;
            ProtectionAdminOutput result;
            try
            {
                result = await reader.ReadAsync(new ProtectionAdminRequest(profile, SyntheticOrganization, SyntheticToken,
                    purview ? SyntheticComplianceToken : null), ct);
            }
            catch (ExchangeAdminTransportException ex)
            {
                output.WriteLine($"FALHA no cenário sintético ({profile}): o adaptador não devolveu documento de resultado. " + ex.Message);
                return 1;
            }

            var e = result.Exchange;
            output.WriteLine($"cenário sintético {profile}: conectado={e.Connected} categoria={e.ConnectionErrorCategory ?? "(nenhuma)"} "
                + $"diagnóstico={e.ConnectionErrorId ?? "(nenhum)"} leituras={e.Reads.Count}"
                + (purview ? $" security-compliance conectado={result.ComplianceConnected} categoria={result.ComplianceErrorCategory ?? "(nenhuma)"}" : ""));

            if (e.Connected || (purview && result.ComplianceConnected))
            {
                output.WriteLine($"FALHA ({profile}): o cenário sintético não pode resultar em conexão estabelecida.");
                return 1;
            }
            if (string.IsNullOrWhiteSpace(e.ConnectionErrorCategory) || (purview && string.IsNullOrWhiteSpace(result.ComplianceErrorCategory)))
            {
                output.WriteLine($"FALHA ({profile}): o script não classificou a falha de conexão.");
                return 1;
            }
            if (string.Equals(e.ConnectionErrorId, "DocumentoDeResultadoIndisponivel", StringComparison.Ordinal))
            {
                output.WriteLine($"FALHA ({profile}): o script caiu no documento mínimo de emergência — a montagem do resultado quebrou.");
                return 1;
            }
            if (purview && !e.Reads.Any(r => !r.Ok && r.Capability == "PurviewDlpPolicies"))
            {
                output.WriteLine("FALHA (purview): a leitura de DLP não registrou o motivo da conexão do Security & Compliance.");
                return 1;
            }

            var saida = string.Join(" ", new[]
            {
                e.ConnectionErrorCategory, e.ConnectionErrorId, result.ComplianceErrorCategory,
                string.Join(" ", e.Reads.Select(r => r.Command + " " + r.ErrorCategory + " " + r.ErrorId)),
            });
            if (saida.Contains(SyntheticToken, StringComparison.Ordinal) || saida.Contains(SyntheticComplianceToken, StringComparison.Ordinal))
            {
                output.WriteLine($"FALHA ({profile}): o marcador sintético de segredo apareceu na saída do adaptador.");
                return 1;
            }
        }
        output.WriteLine("coleta-sintetica=OK");

        output.WriteLine(ScopeMarker);
        output.WriteLine(
            "  PROVADO: o módulo fixado CARREGA nesta imagem; os comandos de conexão (Exchange Online e Security & Compliance) "
            + "existem nesta versão; o adaptador real executa o script embutido nos dois perfis (Defender e Purview) e, sem rede, "
            + "devolve documento de resultado íntegro, sem vazar o token sintético.");
        output.WriteLine(
            "  NÃO PROVADO: autenticação; aceitação do token pelo locatário; os comandos de leitura, que só existem depois da "
            + "conexão; e qualquer coleta real de configuração.");
        output.WriteLine(SuccessMarker);
        return 0;
    }
}
