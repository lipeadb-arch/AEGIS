using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Connectors.Microsoft.Knight.PowerShell;

namespace AegisScore.Connectors.Microsoft.Knight.Exchange;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-03] Porta do adaptador de coleta do Exchange Online
// ============================================================================
// O KNIGHT não conhece PowerShell: conhece esta porta. Quem a implementa de verdade executa o script embutido
// num processo isolado (ver PowerShellExchangeAdminReader); os testes a implementam com respostas sintéticas, o
// que permite exercitar coleta → ADM → releitura → avaliação → exportação sem locatário real e sem processo
// externo.

/// <summary>
/// Credenciais de UMA coleta do Exchange Online.
///
/// <para>São DOIS valores, e nenhum deles é intercambiável com os do Teams ou do Graph:</para>
/// <list type="bullet">
///   <item><b>AccessToken</b> — emitido para o recurso <c>https://outlook.office365.com</c>. Um token do
///   Microsoft Graph não vale aqui, e entregá-lo seria mandar um token a um destino para o qual ele não foi
///   emitido.</item>
///   <item><b>Organization</b> — o domínio <c>.onmicrosoft.com</c> principal do locatário. A documentação da
///   conexão de aplicativo exige este parâmetro junto do token; o identificador do locatário não é o valor
///   documentado.</item>
/// </list>
/// Existem só em memória, pelo tempo da coleta.
/// </summary>
public sealed record ExchangeAdminCredentials(string Organization, string AccessToken)
{
    // Record imprime todas as propriedades no ToString(): aqui isso seria vazar um bearer token.
    public override string ToString() => $"ExchangeAdminCredentials {{ Organization = {Organization}, AccessToken = *** }}";
}

/// <summary>Desfecho de UMA leitura do adaptador (um comando oficial), já normalizado.</summary>
/// <param name="Capability">Nome da capacidade do KNIGHT que esta leitura alimenta.</param>
/// <param name="Command">Comando oficial executado — registrado para rastreabilidade do método de coleta.</param>
/// <param name="Items">Itens devolvidos, sempre um array JSON (vazio quando a leitura não devolveu nada).</param>
/// <param name="Truncated">
/// A enumeração atingiu o TETO de leitura. Distinção que muda o veredito: uma lista truncada em que nada foi
/// encontrado NÃO é uma organização sem problema — é uma leitura que não terminou.
/// </param>
/// <param name="ErrorCategory">Classificação da falha, quando houve. É DAQUI que sai a mensagem do cliente.</param>
/// <param name="ErrorId">
/// Identificador TÉCNICO do erro, já restrito a um conjunto fixo de caracteres e sanitizado. Serve ao
/// diagnóstico do operador e NÃO vira texto de achado.
/// </param>
public sealed record ExchangeAdminRead(
    string Capability,
    string Command,
    bool Ok,
    JsonElement Items,
    bool Truncated,
    string? ErrorCategory,
    string? ErrorId);

/// <summary>Runtime em que a coleta correu — o que a homologação precisa saber para reproduzir o resultado.</summary>
public sealed record ExchangeAdminRuntime(string? PowerShell, string? Module, string? Platform);

/// <summary>
/// Saída completa de UMA execução do adaptador.
/// </summary>
/// <param name="EnumerationLimit">
/// Teto de registros por enumeração que VIGOROU nesta execução. Fica na saída — e não só na configuração —
/// porque o relatório precisa dizer o teto real da coleta que produziu o veredito.
/// </param>
public sealed record ExchangeAdminOutput(
    ExchangeAdminRuntime Runtime,
    bool Connected,
    string? ConnectionErrorId,
    string? ConnectionErrorCategory,
    int? EnumerationLimit,
    IReadOnlyList<ExchangeAdminRead> Reads);

/// <summary>
/// Falha do TRANSPORTE — antes de qualquer leitura: runtime ausente, processo que não terminou no tempo
/// previsto, saída ilegível. É distinta de uma leitura que falhou: aqui nenhuma capacidade foi tentada.
/// </summary>
public sealed class ExchangeAdminTransportException : PowerShellAdapterException
{
    public ExchangeAdminTransportException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Executa o adaptador de coleta do Exchange Online. Somente leitura; nenhuma alteração no locatário.</summary>
public interface IExchangeAdminReader
{
    /// <summary>Conecta como aplicativo e executa as leituras fixas. A sessão é encerrada ao fim, sempre.</summary>
    Task<ExchangeAdminOutput> ReadAsync(ExchangeAdminCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Validação de RUNTIME: importa o módulo e confere que os comandos DO MÓDULO existem nesta plataforma.
    ///
    /// <para><b>O que ela não prova, e é preciso dizer:</b> os comandos de leitura do Exchange (Get-Mailbox,
    /// Get-OrganizationConfig e os demais) NÃO existem antes de uma conexão — eles são importados pela própria
    /// conexão com o locatário. Uma verificação offline não os encontra, e encontrá-los também não diria nada
    /// sobre a AUTORIZAÇÃO para executá-los. Por isso o adaptador os declara como contrato, e não como
    /// verificação aprovada.</para>
    /// </summary>
    Task<ExchangeAdminOutput> CheckRuntimeAsync(CancellationToken ct = default);
}
