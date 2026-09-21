using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AegisScore.Connectors.Microsoft.Knight.Teams;

/// <summary>
/// Opções do adaptador. Nenhuma delas vem do cliente: são do AMBIENTE DE IMPLANTAÇÃO (configuração do próprio
/// AEGIS). O locatário não escolhe executável, caminho de módulo, comando nem tempo limite.
/// </summary>
public sealed class TeamsPowerShellOptions
{
    /// <summary>Executável do PowerShell 7. Em Linux/contêiner, o caminho instalado na imagem.</summary>
    public string Executable { get; set; } = "pwsh";

    /// <summary>Diretório com o módulo MicrosoftTeams pré-instalado (imagem offline). Vazio = padrão da máquina.</summary>
    public string? ModulePath { get; set; }

    /// <summary>Tempo máximo de UMA coleta. Ao estourar, o processo e seus filhos são encerrados.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Teto de saída aceita do processo — defesa contra uma resposta absurda consumir memória.</summary>
    public int MaxOutputBytes { get; set; } = 16 * 1024 * 1024;
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

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly SemaphoreSlim ScriptGate = new(1, 1);
    private static string? _scriptPath;

    private readonly TeamsPowerShellOptions _options;
    private readonly ILogger<PowerShellTeamsAdminReader>? _log;

    public PowerShellTeamsAdminReader(TeamsPowerShellOptions options, ILogger<PowerShellTeamsAdminReader>? log = null)
    {
        _options = options ?? new TeamsPowerShellOptions();
        _log = log;
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
        var script = await EnsureScriptAsync(ct);

        var psi = new ProcessStartInfo
        {
            FileName = _options.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);

        if (!string.IsNullOrWhiteSpace(_options.ModulePath))
        {
            // Imagem OFFLINE: o módulo já está na imagem; nada é baixado em tempo de execução.
            var existing = Environment.GetEnvironmentVariable("PSModulePath");
            psi.Environment["PSModulePath"] = string.IsNullOrWhiteSpace(existing)
                ? _options.ModulePath!
                : _options.ModulePath + Path.PathSeparator + existing;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new TeamsAdminTransportException(
                "O runtime do PowerShell não pôde ser iniciado neste ambiente. A coleta do Microsoft Teams não foi tentada.", ex);
        }

        var stdout = ReadBoundedAsync(process.StandardOutput, _options.MaxOutputBytes, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, 64 * 1024, timeout.Token);

        try
        {
            try
            {
                await process.StandardInput.WriteAsync(stdinPayload.AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
            }
            catch (IOException)
            {
                // O processo fechou a entrada antes de receber o pedido — quase sempre porque JÁ TERMINOU
                // (runtime ausente, argumento recusado, falha na importação do módulo). Isso não é o fim do
                // diagnóstico: o motivo está no erro-padrão e no código de saída. Trocar essa causa por “a
                // comunicação falhou” apagaria justamente a informação útil, então seguimos para lê-la.
            }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { /* cano já rompido: nada a fechar */ }
            }

            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (ct.IsCancellationRequested) throw;
            throw new TeamsAdminTransportException(
                $"A coleta do Microsoft Teams não terminou em {_options.Timeout.TotalMinutes:0} minuto(s) e foi encerrada.");
        }
        catch (IOException ex)
        {
            KillTree(process);
            throw new TeamsAdminTransportException("A comunicação com o processo de coleta do Microsoft Teams falhou.", ex);
        }

        var output = (await stdout).Trim();
        var errors = (await stderr).Trim();

        // O que o processo escreveu em erro-padrão é DIAGNÓSTICO DO OPERADOR, nunca conteúdo do relatório — e é
        // texto de TERCEIRO: o módulo pode repetir ali o cabeçalho de autorização, o token da chamada ou o corpo
        // bruto da resposta. Por isso o texto é SANITIZADO (reescrito) antes de sair daqui, e não apenas
        // truncado: um corte por tamanho preservaria exatamente o começo, que é onde o segredo costuma estar.
        var safe = TeamsDiagnosticScrubber.Scrub(errors, knownSecrets);

        if (safe.Length > 0)
            _log?.LogWarning(
                "Adaptador do Microsoft Teams escreveu {Bytes} caractere(s) em erro-padrão. Diagnóstico sanitizado: {Detalhe}",
                errors.Length, safe);

        if (output.Length == 0)
            // Sem resultado, o erro-padrão JÁ SANITIZADO é a única pista da causa — e sem ela o operador fica com
            // "terminou com código 1" e mais nada. O texto entra aqui porque passou pela sanitização; o que chega
            // ao ADM e ao relatório continua sendo a mensagem montada pelo coletor, nunca este diagnóstico.
            throw new TeamsAdminTransportException(
                $"O adaptador do Microsoft Teams terminou com código {process.ExitCode} sem devolver resultado."
                + (safe.Length > 0 ? " Diagnóstico do processo: " + safe : " O processo não escreveu diagnóstico algum."));

        return Parse(output);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Encerrar é o melhor esforço: o processo pode ter terminado entre a verificação e a chamada.
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
            if (read == 0) break;
            if (builder.Length + read > maxBytes) break;
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
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

    // ---- Script embutido -------------------------------------------------------------------------------

    /// <summary>
    /// Materializa o script embutido UMA vez por processo, num diretório temporário próprio. O conteúdo vem do
    /// assembly — não de disco, nem de configuração, nem do cliente.
    /// </summary>
    private async Task<string> EnsureScriptAsync(CancellationToken ct)
    {
        if (_scriptPath is { } cached && File.Exists(cached)) return cached;

        await ScriptGate.WaitAsync(ct);
        try
        {
            if (_scriptPath is { } again && File.Exists(again)) return again;

            await using var stream = typeof(PowerShellTeamsAdminReader).Assembly.GetManifestResourceStream(ScriptResourceName)
                ?? throw new TeamsAdminTransportException($"Recurso {ScriptResourceName} ausente do assembly.");

            var dir = Directory.CreateTempSubdirectory("aegis-teams-");
            var path = Path.Combine(dir.FullName, "teams-collect.ps1");
            await using (var file = File.Create(path))
            {
                await stream.CopyToAsync(file, ct);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            _scriptPath = path;
            return path;
        }
        finally
        {
            ScriptGate.Release();
        }
    }
}
