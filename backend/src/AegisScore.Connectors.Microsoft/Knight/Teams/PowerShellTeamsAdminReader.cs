using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Connectors.Microsoft.Knight.PowerShell;

namespace AegisScore.Connectors.Microsoft.Knight.Teams;

/// <summary>
/// Opções do adaptador. Nenhuma delas vem do cliente: são do AMBIENTE DE IMPLANTAÇÃO (configuração do próprio
/// AEGIS). O locatário não escolhe executável, caminho de módulo, comando nem tempo limite.
/// </summary>
public sealed class TeamsPowerShellOptions : PowerShellAdapterOptions
{
}

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] Executa o adaptador de coleta do Teams num PROCESSO SEPARADO e descartável.
///
/// Por que um processo por coleta, e não uma sessão hospedada em memória:
///   • ISOLAMENTO ENTRE CLIENTES — o módulo mantém estado de conexão no processo. Um processo novo por coleta
///     torna impossível que a sessão de um locatário alcance a coleta de outro, mesmo sob erro;
///   • CANCELAMENTO E TEMPO LIMITE — encerrar um processo (com a árvore de filhos) é determinístico; abortar
///     uma sessão hospedada não é;
///   • SUPERFÍCIE — o script é RECURSO EMBUTIDO e é a única coisa executada. Não há comando, parâmetro nem
///     endereço vindos do cliente.
///
/// Os tokens são entregues pela ENTRADA PADRÃO, nunca por argumento nem por variável de ambiente: em Linux a
/// linha de comando de um processo é legível por outros processos da máquina e o ambiente aparece em despejos
/// de diagnóstico. Nada do que sai do processo é registrado em log sem sanitização.
/// </summary>
public sealed class PowerShellTeamsAdminReader : ITeamsAdminReader
{
    /// <summary>Nome lógico do recurso embutido com o script de coleta.</summary>
    private const string ScriptResourceName = "AegisScore.Knight.Teams.Collect.ps1";

    private readonly PowerShellAdapterProcess _process;

    public PowerShellTeamsAdminReader(TeamsPowerShellOptions options, ILogger<PowerShellTeamsAdminReader>? log = null)
    {
        // [AEGIS-KNIGHT-COVERAGE-03] O transporte (processo descartável, entrada padrão, tempo limite, árvore de
        // processos, materialização do script e sanitização do diagnóstico) foi EXTRAÍDO para
        // PowerShellAdapterProcess e é o mesmo do adaptador do Exchange Online. Duas cópias significariam
        // corrigir num arquivo um defeito que continuaria vivo no outro — e este transporte já custou duas
        // revisões dirigidas para chegar onde está.
        _process = new PowerShellAdapterProcess(
            options ?? new TeamsPowerShellOptions(),
            ScriptResourceName, "teams-collect.ps1", "aegis-teams-", "Microsoft Teams", log);
    }

    public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var payload = JsonSerializer.Serialize(new
        {
            mode = "collect",
            tenantId = credentials.TenantId,
            graphToken = credentials.GraphToken,
            teamsToken = credentials.TeamsToken,
        });
        // Os tokens desta coleta são entregues à sanitização do diagnóstico: se o módulo repetir um deles num
        // texto de erro, ele é removido por IGUALDADE, sem depender de nenhuma heurística acertar a forma.
        return RunAsync(payload, [credentials.GraphToken, credentials.TeamsToken], ct);
    }

    public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) =>
        RunAsync(JsonSerializer.Serialize(new { mode = "module-check" }), null, ct);

    // ---- Processo -------------------------------------------------------------------------------------

    private async Task<TeamsAdminOutput> RunAsync(
        string stdinPayload, IReadOnlyList<string?>? knownSecrets, CancellationToken ct)
    {
        PowerShellAdapterRun run;
        try
        {
            run = await _process.RunAsync(stdinPayload, knownSecrets, ct);
        }
        catch (PowerShellAdapterException ex)
        {
            // O transporte compartilhado já monta a mensagem com o rótulo deste adaptador e com o diagnóstico
            // SANITIZADO. Ela é reembalada no tipo que o coletor do Teams conhece, sem reescrever o texto.
            throw new TeamsAdminTransportException(ex.Message, ex.InnerException);
        }

        return Parse(run.StandardOutput);
    }

    // ---- Leitura da saída ------------------------------------------------------------------------------

    internal static TeamsAdminOutput Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TeamsAdminTransportException("O adaptador do Microsoft Teams devolveu um resultado ilegível.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var runtime = root.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Object
                ? new TeamsAdminRuntime(Text(rt, "powerShell"), Text(rt, "module"), Text(rt, "platform"))
                : new TeamsAdminRuntime(null, null, null);

            var reads = new List<TeamsAdminRead>();
            if (root.TryGetProperty("reads", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in arr.EnumerateArray())
                {
                    var items = r.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array
                        ? it.Clone()
                        : EmptyArray();
                    reads.Add(new TeamsAdminRead(
                        Text(r, "capability") ?? "",
                        Text(r, "command") ?? "",
                        r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                        items,
                        Text(r, "errorCategory"),
                        // Segunda barreira: o script já restringe o identificador na origem, mas quem lê a saída
                        // não pode depender disso — o que entra no objeto é sempre o valor sanitizado.
                        TeamsDiagnosticScrubber.ScrubIdentifier(Text(r, "errorId"))));
                }
            }

            return new TeamsAdminOutput(
                runtime,
                root.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True,
                TeamsDiagnosticScrubber.ScrubIdentifier(Text(root, "connectionErrorId")),
                Text(root, "connectionErrorCategory"),
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
