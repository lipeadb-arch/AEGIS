using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Onboarding: cadastro do cliente, suas áreas/processos e conectores.
///
/// [Alto 3 / Médio 6] O tenant NUNCA vem da rota (IDOR latente eliminado): as escritas escopadas a um
/// tenant derivam o TenantId exclusivamente do contexto ambiente (claim do JWT, via ITenantContext), e
/// o StampTenant do DbContext revalida na gravação (fail-closed). Escritas de configuração exigem o
/// papel TenantAdmin; criar um novo tenant é operação de PLATAFORMA (PlatformAdmin), fora do fluxo de
/// um tenant comum.
/// </summary>
[ApiController]
[Route("api/v1/tenants")]
[Authorize]
public class TenantsController : ControllerBase
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ITenantManagementService _onboarding;

    public TenantsController(
        AegisScoreDbContext db, ITenantContext tenant, ITenantManagementService onboarding)
    {
        _db = db;
        _tenant = tenant;
        _onboarding = onboarding;
    }

    /// <summary>
    /// Tenant ambiente resolvido pelo JWT. Garantido não-nulo numa rota autenticada — o
    /// TenantConsistencyMiddleware barra (403) qualquer token sem claim tenant_id válida.
    /// </summary>
    private Guid CurrentTenantId => _tenant.TenantId
        ?? throw new InvalidOperationException("Rota autenticada sem tenant no contexto.");

    /// <summary>
    /// [Alto 3] Cria um novo tenant. Operação de PLATAFORMA — exige PlatformAdmin, papel que nenhum
    /// usuário de tenant comum possui (provisionado fora do onboarding self-service).
    ///
    /// A regra (normalização do slug, unicidade sob corrida, estado inicial) vive no
    /// <see cref="ITenantManagementService"/>; aqui só traduzimos o desfecho em HTTP.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "PlatformAdmin")]
    public async Task<ActionResult<IdResponse>> Create(CreateTenantRequest req, CancellationToken ct)
    {
        var result = await _onboarding.CreateTenantAsync(new CreateTenantCommand(req.Name, req.Slug), ct);

        return result.Status switch
        {
            TenantProvisioningStatus.Created => new IdResponse(result.TenantId),
            TenantProvisioningStatus.SlugAlreadyInUse =>
                Conflict($"Já existe um cliente com o slug '{result.Slug}'."),
            _ => BadRequest(
                "Nome obrigatório e slug entre 2 e 64 caracteres, apenas letras minúsculas, dígitos e hífens."),
        };
    }

    [HttpPost("business-units")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<ActionResult<IdResponse>> AddBusinessUnit(
        CreateBusinessUnitRequest req, CancellationToken ct)
    {
        var bu = new BusinessUnit
        {
            TenantId = CurrentTenantId,   // do JWT, nunca da rota; StampTenant revalida
            Name = req.Name,
            Code = req.Code,
            ManagerName = req.ManagerName,
            ManagerEmail = req.ManagerEmail,
        };
        _db.BusinessUnits.Add(bu);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(bu.Id);
    }

    [HttpPost("processes")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<ActionResult<IdResponse>> AddProcess(
        CreateProcessRequest req, CancellationToken ct)
    {
        var p = new BusinessProcess
        {
            TenantId = CurrentTenantId,
            Name = req.Name,
            ProcessCategory = req.ProcessCategory,
            Classification = req.Classification,
            ProcessValue = req.ProcessValue,
        };
        _db.Processes.Add(p);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(p.Id);
    }

    /// <summary>
    /// Configura um conector do tenant ambiente. É UPSERT pela chave natural (Provider + Capability):
    /// repetir a chamada RECONFIGURA o conector, em vez de empilhar duplicatas ambíguas.
    ///
    /// [Médio 6/Baixo] O segredo (tokens OAuth, API keys) é cifrado NO SERVIDOR via Data Protection —
    /// nunca confiado pré-cifrado do cliente. Em claro só trafega dentro do TLS; no banco fica cifrado, e
    /// a decifragem ocorre no momento da coleta. A cifragem agora vive no serviço de aplicação: o
    /// controller não toca mais no segredo, e a resposta jamais o ecoa.
    /// </summary>
    [HttpPost("connectors")]
    [Authorize(Roles = "TenantAdmin")]
    public async Task<ActionResult<ConnectorConfigDto>> ConfigureConnector(
        CreateConnectorRequest req, CancellationToken ct)
    {
        // O TenantId NÃO é passado: o serviço o deriva do contexto (claim do JWT) e o DbContext o
        // revalida no carimbo de gravação.
        var result = await _onboarding.ConfigureConnectorAsync(
            new ConfigureConnectorCommand(
                req.Provider, req.Capability, req.DisplayName, req.AuthType,
                req.Settings, req.SyncIntervalMinutes),
            ct);

        var dto = new ConnectorConfigDto(
            result.ConnectorId, result.Provider.ToString(), result.Capability.ToString(),
            result.DisplayName, result.AuthType.ToString(), result.Enabled,
            result.SyncIntervalMinutes, result.LastSyncAt, result.LastStatus.ToString(),
            result.HasCredentials);

        // 201 na criação, 200 na reconfiguração — o cliente distingue os dois desfechos do upsert.
        // Sem Location: o conector ainda não tem GET canônico (ConnectorsController só opera test/sync),
        // e um CreatedAtAction apontando de volta para este POST seria uma URL mentirosa.
        return result.Created ? StatusCode(StatusCodes.Status201Created, dto) : Ok(dto);
    }
}
