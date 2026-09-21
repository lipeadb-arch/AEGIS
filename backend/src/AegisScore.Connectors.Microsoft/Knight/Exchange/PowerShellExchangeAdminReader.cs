using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Connectors.Microsoft.Knight.PowerShell;

namespace AegisScore.Connectors.Microsoft.Knight.Exchange;

/// <summary>
/// Opções do adaptador do Exchange Online. Nenhuma delas vem do cliente: são do AMBIENTE DE IMPLANTAÇÃO
/// (configuração do próprio AEGIS). O locatário não escolhe executável, caminho de módulo, comando, tempo
/// limite nem teto de enumeração.
/// </summary>
public sealed class ExchangePowerShellOptions : PowerShellAdapterOptions
{
    /// <summary>
    /// Teto de registros por enumeração (caixas de correio, contas, associações). Existe por duas razões, e as
    /// duas importam: conter o tamanho da resposta que atravessa a fronteira do processo, e tornar EXPLÍCITO o
    /// limite da leitura. Uma enumeração que atinge este teto é declarada truncada — e uma leitura truncada em
    /// que nada foi encontrado nunca aprova a organização inteira.
    /// </summary>
    public int EnumerationLimit { get; set; } = 5000;
}

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-03] Executa o adaptador de coleta do Exchange Online num PROCESSO separado e
/// descartável, pelo transporte COMPARTILHADO com o adaptador do Microsoft Teams
/// (<see cref="PowerShellAdapterProcess"/>) — as decisões de isolamento, tempo limite, entrega de segredo pela
/// entrada padrão e sanitização do diagnóstico estão lá, numa implementação só.
///
/// O que é específico deste adaptador e mora aqui: o pedido enviado ao script (token do recurso do Exchange e
/// domínio da organização), o teto de enumeração e a leitura do documento de saída.
/// </summary>
public sealed class PowerShellExchangeAdminReader : IExchangeAdminReader
{
    /// <summary>Nome lógico do recurso embutido com o script de coleta.</summary>
    private const string ScriptResourceName = "AegisScore.Knight.Exchange.Collect.ps1";

    private readonly PowerShellAdapterProcess _process;
    private readonly ExchangePowerShellOptions _options;

    public PowerShellExchangeAdminReader(ExchangePowerShellOptions options, ILogger<PowerShellExchangeAdminReader>? log = null)
    {
        _options = options ?? new ExchangePowerShellOptions();
        _process = new PowerShellAdapterProcess(
            _options, ScriptResourceName, "exchange-collect.ps1", "aegis-exchange-", "Exchange Online", log);
    }

    public Task<ExchangeAdminOutput> ReadAsync(ExchangeAdminCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var payload = JsonSerializer.Serialize(new
        {
            mode = "collect",
            organization = credentials.Organization,
            accessToken = credentials.AccessToken,
            enumerationLimit = _options.EnumerationLimit,
        });
        // O token desta coleta é entregue à sanitização do diagnóstico: se o módulo o repetir num texto de erro,
        // ele é removido por IGUALDADE, sem depender de nenhuma heurística acertar a forma.
        return RunAsync(payload, [credentials.AccessToken], ct);
    }

    public Task<ExchangeAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) =>
        RunAsync(JsonSerializer.Serialize(new { mode = "module-check" }), null, ct);

    private async Task<ExchangeAdminOutput> RunAsync(
        string stdinPayload, IReadOnlyList<string?>? knownSecrets, CancellationToken ct)
    {
        PowerShellAdapterRun run;
        try
        {
            run = await _process.RunAsync(stdinPayload, knownSecrets, ct);
        }
        catch (ExchangeAdminTransportException)
        {
            throw;
        }
        catch (PowerShellAdapterException ex)
        {
            throw new ExchangeAdminTransportException(ex.Message, ex.InnerException);
        }

        return Parse(run.StandardOutput);
    }

    // ---- Leitura da saída ------------------------------------------------------------------------------

    internal static ExchangeAdminOutput Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ExchangeAdminTransportException("O adaptador do Exchange Online devolveu um resultado ilegível.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var runtime = root.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Object
                ? new ExchangeAdminRuntime(Text(rt, "powerShell"), Text(rt, "module"), Text(rt, "platform"))
                : new ExchangeAdminRuntime(null, null, null);

            var reads = new List<ExchangeAdminRead>();
            if (root.TryGetProperty("reads", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in arr.EnumerateArray())
                {
                    var items = r.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array
                        ? it.Clone()
                        : EmptyArray();
                    reads.Add(new ExchangeAdminRead(
                        Text(r, "capability") ?? "",
                        Text(r, "command") ?? "",
                        r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                        items,
                        r.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True,
                        Text(r, "errorCategory"),
                        // Segunda barreira: o script já restringe o identificador na origem, mas quem lê a saída
                        // não pode depender disso — o que entra no objeto é sempre o valor sanitizado.
                        PowerShellDiagnosticScrubber.ScrubIdentifier(Text(r, "errorId"))));
                }
            }

            return new ExchangeAdminOutput(
                runtime,
                root.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True,
                PowerShellDiagnosticScrubber.ScrubIdentifier(Text(root, "connectionErrorId")),
                Text(root, "connectionErrorCategory"),
                root.TryGetProperty("enumerationLimit", out var lim) && lim.ValueKind == JsonValueKind.Number
                    && lim.TryGetInt32(out var limit)
                    ? limit
                    : null,
                reads);
        }
    }

    private static JsonElement EmptyArray()
    {
        using var empty = JsonDocument.Parse("[]");
        return empty.RootElement.Clone();
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
