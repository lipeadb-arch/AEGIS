using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] O TRANSPORTE do adaptador de coleta do Teams: o script EMBUTIDO (é ele, e só ele,
/// que o processo do PowerShell executa) e o comportamento do processo quando o runtime não está disponível.
///
/// O que esta bateria NÃO faz — e por isso não se confunde com homologação: ela não executa o PowerShell nem
/// conecta em locatário nenhum. A execução REAL do runtime e a importação do módulo oficial são provadas
/// DENTRO da imagem Linux de implantação, no passo "Teams collection runtime inside the image" do CI, onde o
/// módulo está instalado. Aqui ficam as garantias que independem do runtime.
/// </summary>
public sealed class TeamsAdapterTransportTests
{
    private const string ResourceName = "AegisScore.Knight.Teams.Collect.ps1";

    private static string Script()
    {
        using var stream = typeof(PowerShellTeamsAdminReader).Assembly.GetManifestResourceStream(ResourceName);
        stream.Should().NotBeNull("o adaptador é um recurso embutido — não um arquivo solto no disco do servidor");
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void ScriptEmbutido_ExecutaSomenteComandosDeLeituraFixos()
    {
        var script = Script();

        // Todos os comandos que o adaptador executa — e todos são de LEITURA (verbo Get), exceto conectar e
        // desconectar a sessão. Nenhum outro verbo pode aparecer.
        foreach (var comando in new[]
        {
            "Get-CsTeamsClientConfiguration", "Get-CsTenantFederationConfiguration", "Get-CsTeamsMeetingPolicy",
            "Get-CsTeamsMessagingPolicy", "Get-CsTeamsAppPermissionPolicy", "Get-CsGroupPolicyAssignment",
        })
            script.Should().Contain(comando);

        foreach (var proibido in new[] { "Set-Cs", "New-Cs", "Remove-Cs", "Grant-Cs", "Update-Cs", "Invoke-RestMethod", "Invoke-WebRequest" })
            script.Should().NotContain(proibido, $"o adaptador é somente leitura e não fala com endereço nenhum por conta própria ({proibido})");

        // A sessão é sempre encerrada, e a conexão é a de APLICATIVO por tokens (não por certificado).
        script.Should().Contain("Disconnect-MicrosoftTeams");
        script.Should().Contain("Connect-MicrosoftTeams -AccessTokens");
        script.Should().NotContain("-CertificateThumbprint");

        // Os tokens entram pela ENTRADA PADRÃO — nunca por argumento de linha de comando nem por ambiente.
        script.Should().Contain("[Console]::In.ReadToEnd()");
    }

    [Fact]
    public void ScriptEmbutido_NaoAceitaComandoVindoDoCliente()
    {
        var script = Script();

        // Nada que transforme entrada em código: o locatário fornece tokens, não comandos.
        foreach (var perigoso in new[] { "Invoke-Expression", "iex ", "[scriptblock]::Create", "& $request", "Start-Process" })
            script.Should().NotContain(perigoso, "nenhuma entrada do cliente pode virar comando executado");
    }

    [Fact]
    public async Task RuntimeInexistente_ViraFalhaDeTransporteDeclarada_NuncaResultadoVazio()
    {
        var reader = new PowerShellTeamsAdminReader(new TeamsPowerShellOptions
        {
            Executable = "aegis-pwsh-que-nao-existe",
            Timeout = TimeSpan.FromSeconds(5),
        });

        var erro = await FluentActions
            .Awaiting(() => reader.ReadAsync(new TeamsAdminCredentials("dir", "graph", "teams")))
            .Should().ThrowAsync<TeamsAdminTransportException>();
        erro.Which.Message.Should().Contain("PowerShell");
    }

    [Fact]
    public async Task ScriptEhMaterializadoEmArquivoProprio_ComLeituraRestritaNoUnix()
    {
        // O materializador roda ANTES de tentar o processo; o executável inexistente falha depois disso.
        var reader = new PowerShellTeamsAdminReader(new TeamsPowerShellOptions
        {
            Executable = "aegis-pwsh-que-nao-existe",
            Timeout = TimeSpan.FromSeconds(5),
        });
        await FluentActions.Awaiting(() => reader.CheckRuntimeAsync()).Should().ThrowAsync<TeamsAdminTransportException>();

        var temporarios = Directory.GetDirectories(Path.GetTempPath(), "aegis-teams-*")
            .SelectMany(d => Directory.GetFiles(d, "teams-collect.ps1"))
            .ToList();
        temporarios.Should().NotBeEmpty("o script embutido é escrito num diretório temporário próprio do processo");

        var arquivo = temporarios[0];
        File.ReadAllText(arquivo).Should().Contain("Get-CsTeamsMeetingPolicy");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(arquivo).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                "o script materializado não é legível por outros usuários da máquina");
    }

    [Fact]
    public void LeituraDaSaida_PreservaCategoriaDeFalhaPorComando()
    {
        var saida = PowerShellTeamsAdminReader.Parse("""
            {"runtime":{"powerShell":"7.4.6","module":"7.9.0","platform":"Unix"},
             "connected":true,"connectionError":null,"connectionErrorCategory":null,
             "reads":[
               {"capability":"TeamsMeetingPolicies","command":"Get-CsTeamsMeetingPolicy","ok":false,
                "items":[],"errorCategory":"Throttled","error":"429 Too Many Requests"},
               {"capability":"TeamsMessagingPolicies","command":"Get-CsTeamsMessagingPolicy","ok":true,
                "items":[{"identity":"Global","allowSecurityEndUserReporting":true}],"errorCategory":null,"error":null}]}
            """);

        saida.Runtime.Module.Should().Be("7.9.0");
        saida.Connected.Should().BeTrue();

        var falhou = saida.Reads.Single(r => r.Capability == "TeamsMeetingPolicies");
        falhou.Ok.Should().BeFalse();
        falhou.ErrorCategory.Should().Be("Throttled");
        falhou.Items.GetArrayLength().Should().Be(0);

        var leu = saida.Reads.Single(r => r.Capability == "TeamsMessagingPolicies");
        leu.Ok.Should().BeTrue();
        leu.Items.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void LeituraDaSaida_SemCampoItems_NaoQuebra_EVaziaNaoViraNulo()
    {
        var saida = PowerShellTeamsAdminReader.Parse(
            """{"connected":true,"reads":[{"capability":"TeamsClientConfiguration","command":"Get-CsTeamsClientConfiguration","ok":true}]}""");

        saida.Reads.Single().Items.GetArrayLength().Should().Be(0);
        saida.Runtime.PowerShell.Should().BeNull("o que a fonte não informou permanece nulo");
    }
}
