using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Connectors.Microsoft.Knight.Teams;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-02] Porta do adaptador de coleta do Microsoft Teams
// ============================================================================
// O KNIGHT não conhece PowerShell: conhece esta porta. Quem a implementa de verdade executa o script embutido
// num processo isolado (ver PowerShellTeamsAdminReader); os testes a implementam com respostas sintéticas, o
// que permite exercitar coleta → ADM → releitura → avaliação sem locatário real e sem processo externo.

/// <summary>
/// Credenciais de UMA coleta. Os dois tokens são de RECURSOS DIFERENTES, como a documentação da autenticação de
/// aplicativo do módulo Teams PowerShell exige — o token do Microsoft Graph não é reaproveitado no recurso de
/// administração do Teams. Existem só em memória, pelo tempo da coleta.
/// </summary>
public sealed record TeamsAdminCredentials(string TenantId, string GraphToken, string TeamsToken)
{
    // Record imprime todas as propriedades no ToString(): aqui isso seria vazar dois bearer tokens.
    public override string ToString() => $"TeamsAdminCredentials {{ TenantId = {TenantId}, GraphToken = ***, TeamsToken = *** }}";
}

/// <summary>Desfecho de UMA leitura do adaptador (um comando oficial), já normalizado.</summary>
/// <param name="Capability">Nome da capacidade do KNIGHT que esta leitura alimenta.</param>
/// <param name="Command">Comando oficial executado — registrado para rastreabilidade do método de coleta.</param>
/// <param name="Items">Itens devolvidos, sempre um array JSON (vazio quando a leitura não devolveu nada).</param>
/// <param name="ErrorCategory">Classificação da falha, quando houve; nunca o texto bruto do erro da fonte.</param>
public sealed record TeamsAdminRead(
    string Capability,
    string Command,
    bool Ok,
    JsonElement Items,
    string? ErrorCategory,
    string? Error);

/// <summary>Runtime em que a coleta correu — o que a homologação precisa saber para reproduzir o resultado.</summary>
public sealed record TeamsAdminRuntime(string? PowerShell, string? Module, string? Platform);

/// <summary>Saída completa de UMA execução do adaptador.</summary>
public sealed record TeamsAdminOutput(
    TeamsAdminRuntime Runtime,
    bool Connected,
    string? ConnectionError,
    string? ConnectionErrorCategory,
    IReadOnlyList<TeamsAdminRead> Reads);

/// <summary>
/// Falha do TRANSPORTE — antes de qualquer leitura: runtime ausente, processo que não terminou no tempo
/// previsto, saída ilegível. É distinta de uma leitura que falhou: aqui nenhuma capacidade foi tentada.
/// A mensagem é sanitizada e nunca carrega token, segredo ou caminho de arquivo do cliente.
/// </summary>
public sealed class TeamsAdminTransportException : Exception
{
    public TeamsAdminTransportException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Executa o adaptador de coleta do Teams. Somente leitura; nenhuma alteração no locatário.</summary>
public interface ITeamsAdminReader
{
    /// <summary>Conecta como aplicativo e executa as leituras fixas. A sessão é encerrada ao fim, sempre.</summary>
    Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Validação de RUNTIME: importa o módulo e confere que os comandos usados existem nesta plataforma. NÃO
    /// conecta em locatário nenhum e não precisa de credencial — serve para provar o ambiente de implantação.
    /// </summary>
    Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default);
}
