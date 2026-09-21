using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AegisScore.Connectors.Microsoft.Knight.PowerShell;

/// <summary>
/// Opções de UM adaptador de coleta em PowerShell. Nenhuma delas vem do cliente: são do AMBIENTE DE IMPLANTAÇÃO
/// (configuração do próprio AEGIS). O locatário não escolhe executável, caminho de módulo, comando nem tempo
/// limite.
/// </summary>
public abstract class PowerShellAdapterOptions
{
    /// <summary>Executável do PowerShell 7. Em Linux/contêiner, o caminho instalado na imagem.</summary>
    public string Executable { get; set; } = "pwsh";

    /// <summary>Diretório com o módulo oficial pré-instalado (imagem offline). Vazio = padrão da máquina.</summary>
    public string? ModulePath { get; set; }

    /// <summary>Tempo máximo de UMA coleta. Ao estourar, o processo e seus filhos são encerrados.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Teto de saída aceita do processo — defesa contra uma resposta absurda consumir memória.</summary>
    public int MaxOutputBytes { get; set; } = 16 * 1024 * 1024;
}

/// <summary>
/// Falha do TRANSPORTE — antes de qualquer leitura: runtime ausente, processo que não terminou no tempo
/// previsto, saída ilegível. É distinta de uma leitura que falhou: aqui nenhuma capacidade foi tentada.
/// A mensagem é sanitizada e nunca carrega token, segredo ou caminho de arquivo do cliente.
/// </summary>
public class PowerShellAdapterException : Exception
{
    public PowerShellAdapterException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>O que UMA execução do processo produziu, antes de qualquer interpretação de contrato.</summary>
public sealed record PowerShellAdapterRun(string StandardOutput, string SafeDiagnostics, int ExitCode);

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-03] Execução de um script EMBUTIDO num processo de PowerShell separado e descartável.
///
/// Esta classe é a extração — não a reescrita — do transporte que o adaptador do Microsoft Teams já usava e que
/// passou por duas revisões dirigidas. Cada decisão abaixo custou um defeito real para ser aprendida, e por isso
/// existe UMA implementação, não duas:
///
///   • UM PROCESSO POR COLETA. O módulo mantém estado de conexão no processo. Um processo novo por coleta torna
///     impossível que a sessão de um locatário alcance a coleta de outro, mesmo sob erro. Encerrar um processo
///     (com a árvore de filhos) é determinístico; abortar uma sessão hospedada não é.
///   • SCRIPT EMBUTIDO NO ASSEMBLY. É a única coisa executada. Não há comando, parâmetro nem endereço vindos do
///     cliente.
///   • SEGREDOS PELA ENTRADA PADRÃO. Nunca por argumento nem por variável de ambiente: em Linux a linha de
///     comando de um processo é legível por outros processos da máquina e o ambiente aparece em despejos.
///   • CANO ROMPIDO NÃO APAGA A CAUSA. Quando o processo termina antes de ler a entrada, a escrita quebra o cano
///     e lança. Tratar isso como "a comunicação falhou" substituiria o diagnóstico real (o código de saída e o
///     erro-padrão) por uma mensagem genérica — foi um defeito real do bloco anterior.
///   • DIAGNÓSTICO SANITIZADO, NÃO TRUNCADO. O erro-padrão é texto de TERCEIRO e pode repetir cabeçalho de
///     autorização ou token; um corte por tamanho preservaria justamente o começo, que é onde o segredo costuma
///     estar.
/// </summary>
public sealed class PowerShellAdapterProcess
{
    private readonly PowerShellAdapterOptions _options;
    private readonly string _resourceName;
    private readonly string _scriptFileName;
    private readonly string _tempPrefix;
    private readonly string _adapterLabel;
    private readonly ILogger? _log;

    private readonly SemaphoreSlim _scriptGate = new(1, 1);
    private string? _scriptPath;

    /// <param name="resourceName">Nome lógico do recurso embutido com o script.</param>
    /// <param name="scriptFileName">Nome do arquivo materializado (só para legibilidade do diagnóstico).</param>
    /// <param name="tempPrefix">Prefixo do diretório temporário próprio deste adaptador.</param>
    /// <param name="adapterLabel">Como o adaptador é chamado nas mensagens ao operador.</param>
    public PowerShellAdapterProcess(
        PowerShellAdapterOptions options,
        string resourceName,
        string scriptFileName,
        string tempPrefix,
        string adapterLabel,
        ILogger? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resourceName = resourceName;
        _scriptFileName = scriptFileName;
        _tempPrefix = tempPrefix;
        _adapterLabel = adapterLabel;
        _log = log;
    }

    /// <summary>
    /// Executa o script com o pedido entregue pela ENTRADA PADRÃO. <paramref name="knownSecrets"/> são os
    /// valores que o AEGIS acabou de entregar ao processo: removidos por IGUALDADE do diagnóstico, sem depender
    /// de nenhuma heurística acertar a forma do segredo.
    /// </summary>
    public async Task<PowerShellAdapterRun> RunAsync(
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
            throw new PowerShellAdapterException(
                $"O runtime do PowerShell não pôde ser iniciado neste ambiente. A coleta do {_adapterLabel} não foi tentada.", ex);
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
                // diagnóstico: o motivo está no erro-padrão e no código de saída.
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
            throw new PowerShellAdapterException(
                $"A coleta do {_adapterLabel} não terminou em {_options.Timeout.TotalMinutes:0} minuto(s) e foi encerrada.");
        }
        catch (IOException ex)
        {
            KillTree(process);
            throw new PowerShellAdapterException($"A comunicação com o processo de coleta do {_adapterLabel} falhou.", ex);
        }

        var output = (await stdout).Trim();
        var errors = (await stderr).Trim();
        var safe = PowerShellDiagnosticScrubber.Scrub(errors, knownSecrets);

        if (safe.Length > 0)
            _log?.LogWarning(
                "Adaptador do {Adaptador} escreveu {Bytes} caractere(s) em erro-padrão. Diagnóstico sanitizado: {Detalhe}",
                _adapterLabel, errors.Length, safe);

        if (output.Length == 0)
            // Sem resultado, o erro-padrão JÁ SANITIZADO é a única pista da causa — e sem ela o operador fica com
            // "terminou com código 1" e mais nada. O que chega ao ADM e ao relatório continua sendo a mensagem
            // montada pelo coletor, nunca este diagnóstico.
            throw new PowerShellAdapterException(
                $"O adaptador do {_adapterLabel} terminou com código {process.ExitCode} sem devolver resultado."
                + (safe.Length > 0 ? " Diagnóstico do processo: " + safe : " O processo não escreveu diagnóstico algum."));

        return new PowerShellAdapterRun(output, safe, process.ExitCode);
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

    /// <summary>
    /// Materializa o script embutido UMA vez por instância, num diretório temporário próprio. O conteúdo vem do
    /// assembly — não de disco, nem de configuração, nem do cliente.
    /// </summary>
    private async Task<string> EnsureScriptAsync(CancellationToken ct)
    {
        if (_scriptPath is { } cached && File.Exists(cached)) return cached;

        await _scriptGate.WaitAsync(ct);
        try
        {
            if (_scriptPath is { } again && File.Exists(again)) return again;

            await using var stream = typeof(PowerShellAdapterProcess).Assembly.GetManifestResourceStream(_resourceName)
                ?? throw new PowerShellAdapterException($"Recurso {_resourceName} ausente do assembly.");

            var dir = Directory.CreateTempSubdirectory(_tempPrefix);
            var path = Path.Combine(dir.FullName, _scriptFileName);
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
            _scriptGate.Release();
        }
    }
}
