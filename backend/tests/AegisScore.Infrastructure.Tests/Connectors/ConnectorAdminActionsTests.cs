using AegisScore.Api.Contracts;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Um conector DESCONECTADO (credencial eliminada) não pode ser testado nem
/// sincronizado, e um DESABILITADO não pode ser sincronizado (novas coletas pausadas) — a borda recusa de
/// forma controlada (409), antes de tocar adaptador/executor. Testado direto no <see cref="ConnectorsController"/>
/// com fakes: os desfechos de recusa retornam ANTES de usar registry/executor/scopeFactory, que podem ser nulos.
/// </summary>
public sealed class ConnectorAdminActionsTests
{
    private static ConnectorConfig Cfg(bool enabled, string encryptedSettings, string? ingestionKeyHash = null) =>
        new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(),
            Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.SecureScore,
            DisplayName = "Graph", AuthType = ConnectorAuthType.OAuthClientCredentials,
            Enabled = enabled, EncryptedSettings = encryptedSettings, IngestionKeyHash = ingestionKeyHash,
        };

    private static ConnectorsController ControllerFor(ConnectorConfig cfg) =>
        new(new FakeTenants(cfg), registry: null!, executor: null!, scopeFactory: null!, lifetime: null!,
            NullLogger<ConnectorsController>.Instance);

    private static int? StatusOf(object? result) => (result as ObjectResult)?.StatusCode;

    [Fact]
    public async Task Test_ConectorDesconectado_Recusa409()
    {
        var cfg = Cfg(enabled: true, encryptedSettings: "");   // sem credencial = desconectado
        var result = await ControllerFor(cfg).Test(cfg.Id, default);
        StatusOf(result.Result).Should().Be(409, "testar um conector desconectado é recusado de forma controlada");
    }

    [Fact]
    public async Task Sync_ConectorDesconectado_Recusa409()
    {
        var cfg = Cfg(enabled: true, encryptedSettings: "", ingestionKeyHash: null);
        var result = await ControllerFor(cfg).Sync(cfg.Id, default);
        StatusOf(result).Should().Be(409, "sincronizar um conector desconectado é recusado");
    }

    [Fact]
    public async Task Sync_ConectorDesabilitado_Recusa409()
    {
        var cfg = Cfg(enabled: false, encryptedSettings: "cifrado");   // tem credencial, mas está pausado
        var result = await ControllerFor(cfg).Sync(cfg.Id, default);
        StatusOf(result).Should().Be(409, "conector desabilitado não inicia novas coletas");
    }

    [Fact]
    public async Task Enable_ConectorDesconectado_Recusa409()
    {
        // O serviço devolve MissingCredential (habilitar um desconectado); a borda o traduz em 409 orientado à
        // ação — nunca 400/500 —, para o frontend tratar pelo mesmo caminho dos demais conflitos de estado.
        var cfg = Cfg(enabled: false, encryptedSettings: "");
        var tenants = new FakeTenants(cfg)
        {
            EnabledResult = ConnectorAdminResult.Rejected(
                ConnectorAdminStatus.MissingCredential, "Conector desconectado: reconecte antes de habilitar."),
        };
        var controller = new ConnectorsController(
            tenants, registry: null!, executor: null!, scopeFactory: null!, lifetime: null!,
            NullLogger<ConnectorsController>.Instance);

        var result = await controller.Enable(cfg.Id, default);
        StatusOf(result.Result).Should().Be(409, "habilitar um conector desconectado é conflito de estado");
    }

    /// <summary>ITenantManagementService mínimo: só GetConnectorAsync responde; o resto não é exercitado aqui.</summary>
    private sealed class FakeTenants : ITenantManagementService
    {
        private readonly ConnectorConfig _config;
        public FakeTenants(ConnectorConfig config) => _config = config;

        /// <summary>Desfecho que <see cref="SetConnectorEnabledAsync"/> devolve (quando o teste exercita habilitar/desabilitar).</summary>
        public ConnectorAdminResult? EnabledResult { get; init; }

        public Task<ConnectorConfig?> GetConnectorAsync(Guid connectorId, CancellationToken ct = default) =>
            Task.FromResult<ConnectorConfig?>(connectorId == _config.Id ? _config : null);

        public Task<TenantProvisioningResult> CreateTenantAsync(CreateTenantCommand c, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<ConnectorConfigurationResult> ConfigureConnectorAsync(ConfigureConnectorCommand c, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<ConnectorConfigurationResult>> ConfigureMicrosoftHubAsync(ConfigureMicrosoftHubCommand c, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<ConnectorSummary>> ListConnectorsAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<bool> RecordSyncResultAsync(Guid id, IReadOnlyList<EvidenceSignal> s, ConnectorStatus st, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<ConnectorAdminResult> UpdateConnectorAsync(UpdateConnectorCommand c, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<ConnectorAdminResult> SetConnectorEnabledAsync(Guid id, bool enabled, CancellationToken ct = default) =>
            EnabledResult is not null
                ? Task.FromResult(EnabledResult)
                : throw new NotImplementedException();
        public Task<ConnectorAdminResult> DisconnectConnectorAsync(Guid id, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
