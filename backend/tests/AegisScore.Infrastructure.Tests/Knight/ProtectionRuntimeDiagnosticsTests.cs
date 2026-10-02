using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.Protection;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] O gate de runtime do adaptador de proteção (executado no CI dentro da imagem) só imprime
/// a marca de sucesso quando o módulo importa, os comandos de conexão das DUAS sessões existem e o script, sem rede,
/// devolve documento íntegro nos dois perfis sem vazar o token — e sempre declara o que NÃO provou.
/// </summary>
public sealed class ProtectionRuntimeDiagnosticsTests
{
    [Fact]
    public async Task RuntimeCompleto_ImprimeSucesso_EOAlcanceDoGate()
    {
        var (code, saida) = await Run(new FakeReader());
        code.Should().Be(0, saida);
        saida.Should().Contain("module-check=OK").And.Contain("coleta-sintetica=OK").And.Contain(ProtectionRuntimeDiagnostics.SuccessMarker);
        saida.Should().Contain(ProtectionRuntimeDiagnostics.ScopeMarker).And.Contain("NÃO PROVADO:");
    }

    [Fact]
    public async Task ModuloNaoImportado_Falha()
    {
        var (code, saida) = await Run(new FakeReader { Module = null });
        code.Should().Be(1);
        saida.Should().Contain("não foi importado").And.NotContain(ProtectionRuntimeDiagnostics.SuccessMarker);
    }

    [Fact]
    public async Task SemConnectIppsSession_Falha_NomeandoOComando()
    {
        var (code, saida) = await Run(new FakeReader { ModuleCommands = new[] { "Connect-ExchangeOnline", "Disconnect-ExchangeOnline" } });
        code.Should().Be(1);
        saida.Should().Contain("Connect-IPPSSession");
    }

    [Fact]
    public async Task TokenSinteticoNaSaida_Falha()
    {
        var (code, saida) = await Run(new FakeReader { LeakToken = true });
        code.Should().Be(1);
        saida.Should().Contain("marcador sintético").And.NotContain(ProtectionRuntimeDiagnostics.SuccessMarker);
    }

    [Fact]
    public async Task DocumentoDeEmergencia_Falha()
    {
        var (code, saida) = await Run(new FakeReader { EmergencyDocument = true });
        code.Should().Be(1);
        saida.Should().Contain("documento mínimo de emergência");
    }

    private static async Task<(int, string)> Run(FakeReader reader)
    {
        var w = new StringWriter();
        var code = await ProtectionRuntimeDiagnostics.RunAsync(reader, ct => Task.FromResult(reader.Check()), w);
        return (code, w.ToString());
    }

    private sealed class FakeReader : IProtectionAdminReader
    {
        public string? Module { get; init; } = "3.9.2";
        public string[] ModuleCommands { get; init; } = { "Connect-ExchangeOnline", "Connect-IPPSSession", "Disconnect-ExchangeOnline" };
        public bool LeakToken { get; init; }
        public bool EmergencyDocument { get; init; }

        private static readonly JsonElement Empty = JsonDocument.Parse("[]").RootElement.Clone();

        public ProtectionAdminOutput Check() => new(
            new ExchangeAdminOutput(new ExchangeAdminRuntime("7.4.6", Module, "Linux"), false, null, null, null,
                ModuleCommands.Select(c => new ExchangeAdminRead("ModuleCheck", c, true, Empty, false, null, null)).ToList()),
            false, null);

        public Task<ProtectionAdminOutput> ReadAsync(ProtectionAdminRequest request, CancellationToken ct = default)
        {
            var purview = request.Profile == ProtectionAdminRequest.Purview;
            var reads = new List<ExchangeAdminRead>();
            if (purview)
                reads.Add(new ExchangeAdminRead("PurviewDlpPolicies", "Get-DlpCompliancePolicy", false, Empty, false, "Unavailable",
                    LeakToken ? request.ComplianceToken : "ConnectionFailed"));
            return Task.FromResult(new ProtectionAdminOutput(
                new ExchangeAdminOutput(new ExchangeAdminRuntime("7.4.6", Module, "Linux"), false,
                    EmergencyDocument ? "DocumentoDeResultadoIndisponivel" : "ConnectFailed", "Unavailable", 5000, reads),
                false, purview ? "Unavailable" : null));
        }

        public Task<ProtectionAdminOutput> TestConnectionAsync(ProtectionAdminRequest request, CancellationToken ct = default) =>
            ReadAsync(request, ct);
    }
}
