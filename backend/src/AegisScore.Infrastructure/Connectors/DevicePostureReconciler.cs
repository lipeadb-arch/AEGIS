using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AppPosture = AegisScore.Application.Abstractions.DevicePostureSnapshot;
using AppConfiguration = AegisScore.Application.Abstractions.DevicePostureConfigurationDimension;
using AppDevices = AegisScore.Application.Abstractions.DevicePostureDeviceDimension;
using AppSyncResult = AegisScore.Application.Abstractions.DevicePostureSyncResult;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Reconcilia a fotografia PROVIDER-NEUTRAL da postura de dispositivos no
/// snapshot persistido por (tenant, conector), sob o tenant proprietário (contexto NOVO). As DUAS dimensões são
/// reconciliadas de forma INDEPENDENTE dentro de UMA transação — a sincronização é atômica no limite do snapshot,
/// mas uma dimensão que falhou nunca apaga a outra.
///
/// Regras de substituição HONESTAS, aplicadas por dimensão:
///  • dimensão COMPLETA (Available) → substitui ATOMICAMENTE os filhos daquela dimensão;
///  • dimensão FALHA (NotAuthorized/NotLicensed/Unavailable) → PRESERVA os dados anteriores e só registra o
///    desfecho da tentativa; sem dados anteriores, grava um placeholder honesto (nada coletado ainda);
///  • dimensão PARCIAL que rebaixaria dados COMPLETOS → preserva os completos e registra a tentativa parcial;
///  • primeira coleta parcial (sem completa anterior) → grava o piso, marcado Partial (nunca "completo");
///  • fingerprint idêntico + mesmo estado → só atualiza a recência (impede write desnecessário dos filhos).
///
/// A substituição dos filhos é feita em DUAS gravações dentro da MESMA transação: primeiro o DELETE das linhas
/// antigas, depois o INSERT das novas. Isso libera o índice único natural antes da inserção (portátil entre
/// PostgreSQL e SQLite) e — o ponto sutil — evita ROMPER a navegação do agregado: severar a coleção marcaria os
/// filhos como Modified com a FK composta (Id, TenantId) zerada, o que o guard de escrita multi-tenant recusa,
/// corretamente, como "linha fora do tenant".
///
/// NUNCA cria EvidenceSignal, NUNCA toca TenantControlState/score/NIST, NUNCA persiste identificador de
/// dispositivo, usuário, PII ou payload de política.
/// </summary>
public sealed class DevicePostureReconciler
{
    /// <summary>O que fazer com UMA dimensão nesta reconciliação.</summary>
    private enum DimensionPlan
    {
        /// <summary>Falha, ou parcial que rebaixaria um completo: os dados anteriores ficam intactos.</summary>
        Preserve,
        /// <summary>Mesmos dados (fingerprint idêntico) e mesmo estado: só a recência muda.</summary>
        TouchRecency,
        /// <summary>Dados novos: substitui os filhos e os agregados daquela dimensão.</summary>
        Replace,
    }

    private readonly AegisScoreDbContext _db;
    private readonly ILogger? _log;

    public DevicePostureReconciler(AegisScoreDbContext db, ILogger? log = null)
    {
        _db = db;
        _log = log;
    }

    public async Task<AppSyncResult> ReconcileAsync(Guid connectorId, AppPosture incoming, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var snapshot = await _db.DevicePostureSnapshots
            .Include(s => s.Policies)
            .Include(s => s.DeviceGroups)
            .FirstOrDefaultAsync(s => s.ConnectorConfigId == connectorId, ct);

        if (snapshot is null)
        {
            snapshot = new DevicePostureSnapshot
            {
                ConnectorConfigId = connectorId,
                Source = incoming.Source,
            };
            _db.DevicePostureSnapshots.Add(snapshot);
        }
        else
        {
            snapshot.Source = incoming.Source;
        }

        var configurationFingerprint = ConfigurationFingerprint(incoming.Configuration);
        var deviceFingerprint = DeviceFingerprint(incoming.Devices);
        var configurationPlan = PlanFor(
            incoming.Configuration.State, incoming.Configuration.HasInventory,
            snapshot.ConfigurationState, snapshot.ConfigurationFingerprint, configurationFingerprint);
        var devicePlan = PlanFor(
            incoming.Devices.State, incoming.Devices.HasInventory,
            snapshot.DeviceState, snapshot.DeviceFingerprint, deviceFingerprint);

        // Fase 1 — DELETE das linhas substituídas. Nunca se remove da coleção de navegação (severar a relação
        // zeraria a FK composta e o guard de escrita recusaria a linha); marca-se Deleted no DbSet e descarrega-se.
        var removed = false;
        if (configurationPlan == DimensionPlan.Replace && snapshot.Policies.Count > 0)
        {
            _db.DevicePosturePolicies.RemoveRange(snapshot.Policies);
            removed = true;
        }
        if (devicePlan == DimensionPlan.Replace && snapshot.DeviceGroups.Count > 0)
        {
            _db.DevicePostureDeviceGroups.RemoveRange(snapshot.DeviceGroups);
            removed = true;
        }
        if (removed) await _db.SaveChangesAsync(ct);

        // Fase 2 — agregados e novos filhos. Os filhos entram pelo DbSet com a FK explícita: as instâncias antigas
        // continuam na coleção em memória (já apagadas e destacadas) e não participam mais de nenhuma gravação.
        ApplyConfiguration(snapshot, incoming.Configuration, configurationPlan, configurationFingerprint, now);
        ApplyDevices(snapshot, incoming.Devices, devicePlan, deviceFingerprint, now);
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        var policiesStored = configurationPlan == DimensionPlan.Replace
            ? incoming.Configuration.Policies.Count
            : snapshot.Policies.Count(p => _db.Entry(p).State != EntityState.Detached);
        var groupsStored = devicePlan == DimensionPlan.Replace
            ? incoming.Devices.Groups.Count
            : snapshot.DeviceGroups.Count(g => _db.Entry(g).State != EntityState.Detached);

        _log?.LogInformation(
            "Postura de dispositivos do conector {ConnectorId}: configuração {ConfigState} (atribuição {AssignmentState}), " +
            "dispositivos {DeviceState}; {Policies} política(s) e {Groups} grupo(s) armazenados.",
            connectorId, snapshot.ConfigurationState, snapshot.AssignmentState, snapshot.DeviceState,
            policiesStored, groupsStored);

        // O resumo descreve ESTA sincronização: os estados são os da TENTATIVA (o que acabou de acontecer), não os
        // dos dados preservados. É isso que permite à borda dizer "bloqueada por permissão" em vez de devolver um
        // total como se a dimensão tivesse sido lida agora.
        return new AppSyncResult(
            ConfigurationState: snapshot.ConfigurationAttemptState,
            AssignmentState: snapshot.AssignmentState,
            DeviceState: snapshot.DeviceAttemptState,
            PoliciesStored: policiesStored,
            DeviceGroupsStored: groupsStored,
            TotalDevices: snapshot.TotalDevices,
            ConfigurationPreserved: configurationPlan == DimensionPlan.Preserve,
            DevicesPreserved: devicePlan == DimensionPlan.Preserve);
    }

    /// <summary>
    /// Decide o destino de UMA dimensão, sem tocar o banco. Falha ⇒ preserva; parcial sobre completo ⇒ preserva;
    /// mesmos dados e mesmo estado ⇒ só recência; caso contrário ⇒ substitui.
    /// </summary>
    private static DimensionPlan PlanFor(
        DevicePostureDimensionState incomingState,
        bool incomingHasInventory,
        DevicePostureDimensionState storedState,
        string storedFingerprint,
        string incomingFingerprint)
    {
        if (!incomingHasInventory) return DimensionPlan.Preserve;

        if (incomingState == DevicePostureDimensionState.Partial
            && storedState == DevicePostureDimensionState.Available)
            return DimensionPlan.Preserve;

        if (storedState == incomingState
            && string.Equals(storedFingerprint, incomingFingerprint, StringComparison.Ordinal))
            return DimensionPlan.TouchRecency;

        return DimensionPlan.Replace;
    }

    // ---- Dimensão 1: postura configurada -------------------------------------------------------------

    private void ApplyConfiguration(
        DevicePostureSnapshot snapshot, AppConfiguration incoming,
        DimensionPlan plan, string fingerprint, DateTimeOffset now)
    {
        // O desfecho da TENTATIVA é registrado sempre — inclusive (e principalmente) quando ela falhou.
        snapshot.ConfigurationAttemptState = incoming.State;
        snapshot.ConfigurationAttemptAt = now;

        if (plan == DimensionPlan.Preserve) return;   // dados anteriores intactos; nada de zero sintético

        snapshot.ConfigurationCollectedAt = now;
        if (plan == DimensionPlan.TouchRecency) return;

        foreach (var p in incoming.Policies)
        {
            _db.DevicePosturePolicies.Add(new DevicePosturePolicy
            {
                DevicePostureSnapshotId = snapshot.Id,
                ExternalId = p.ExternalId,
                Kind = p.Kind,
                DisplayName = p.DisplayName,
                PlatformLabel = p.PlatformLabel,
                AssignmentState = p.AssignmentState,
                AssignmentCount = p.AssignmentCount,
                SourceLastModifiedAt = p.SourceLastModifiedAt,
            });
        }

        snapshot.ConfigurationState = incoming.State;
        snapshot.ConfigurationFingerprint = fingerprint;
        snapshot.CompliancePolicyCount = incoming.Policies.Count(p => p.Kind == DevicePolicyKind.CompliancePolicy);
        snapshot.DeviceConfigurationCount = incoming.Policies.Count(p => p.Kind == DevicePolicyKind.DeviceConfiguration);
        snapshot.AssignmentState = incoming.AssignmentState;
        snapshot.PoliciesAssigned = incoming.Policies.Count(p => p.AssignmentState == DevicePolicyAssignmentState.Assigned);
        snapshot.PoliciesUnassigned = incoming.Policies.Count(p => p.AssignmentState == DevicePolicyAssignmentState.Unassigned);
        snapshot.PoliciesAssignmentUnknown = incoming.Policies.Count(p => p.AssignmentState == DevicePolicyAssignmentState.Unknown);
    }

    // ---- Dimensão 2: estado efetivo dos dispositivos --------------------------------------------------

    private void ApplyDevices(
        DevicePostureSnapshot snapshot, AppDevices incoming,
        DimensionPlan plan, string fingerprint, DateTimeOffset now)
    {
        snapshot.DeviceAttemptState = incoming.State;
        snapshot.DeviceAttemptAt = now;

        // Falha (inclusive a ausência da permissão de dispositivos): preserva os totais anteriores. A tela mostra
        // "bloqueada por permissão" — jamais "0 dispositivos não conformes".
        if (plan == DimensionPlan.Preserve) return;

        snapshot.DeviceCollectedAt = now;
        if (plan == DimensionPlan.TouchRecency) return;

        foreach (var g in incoming.Groups)
        {
            _db.DevicePostureDeviceGroups.Add(new DevicePostureDeviceGroup
            {
                DevicePostureSnapshotId = snapshot.Id,
                OperatingSystem = g.OperatingSystem,
                Compliance = g.Compliance,
                Encryption = g.Encryption,
                Activity = g.Activity,
                DeviceCount = g.DeviceCount,
            });
        }

        snapshot.DeviceState = incoming.State;
        snapshot.DeviceFingerprint = fingerprint;
        snapshot.TotalDevices = incoming.TotalDevices;
        snapshot.StaleThresholdDays = incoming.StaleThresholdDays;
        snapshot.DevicesWithDirectoryId = incoming.DevicesWithDirectoryId;

        // Totais DERIVADOS dos grupos — jamais um número paralelo que possa divergir da tabela exibida.
        snapshot.CompliantDevices = SumCompliance(incoming, DeviceComplianceBucket.Compliant);
        snapshot.NoncompliantDevices = SumCompliance(incoming, DeviceComplianceBucket.Noncompliant);
        snapshot.InGracePeriodDevices = SumCompliance(incoming, DeviceComplianceBucket.InGracePeriod);
        snapshot.ConflictDevices = SumCompliance(incoming, DeviceComplianceBucket.Conflict);
        snapshot.ErrorDevices = SumCompliance(incoming, DeviceComplianceBucket.Error);
        snapshot.ManagedExternallyDevices = SumCompliance(incoming, DeviceComplianceBucket.ManagedExternally);
        snapshot.UnknownComplianceDevices = SumCompliance(incoming, DeviceComplianceBucket.Unknown);

        snapshot.EncryptedDevices = SumEncryption(incoming, DeviceEncryptionBucket.Encrypted);
        snapshot.NotEncryptedDevices = SumEncryption(incoming, DeviceEncryptionBucket.NotEncrypted);
        snapshot.UnknownEncryptionDevices = SumEncryption(incoming, DeviceEncryptionBucket.Unknown);

        snapshot.ActiveDevices = SumActivity(incoming, DeviceActivityBucket.Active);
        snapshot.StaleDevices = SumActivity(incoming, DeviceActivityBucket.Stale);
        snapshot.UnknownActivityDevices = SumActivity(incoming, DeviceActivityBucket.Unknown);
    }

    private static int SumCompliance(AppDevices d, DeviceComplianceBucket bucket) =>
        d.Groups.Where(g => g.Compliance == bucket).Sum(g => g.DeviceCount);

    private static int SumEncryption(AppDevices d, DeviceEncryptionBucket bucket) =>
        d.Groups.Where(g => g.Encryption == bucket).Sum(g => g.DeviceCount);

    private static int SumActivity(AppDevices d, DeviceActivityBucket bucket) =>
        d.Groups.Where(g => g.Activity == bucket).Sum(g => g.DeviceCount);

    // ---- Fingerprints determinísticos ------------------------------------------------------------------

    /// <summary>
    /// SHA-256 dos DADOS de políticas (ordenados por família + id), independente da ordem de chegada. NÃO inclui
    /// o estado (comparado à parte) nem qualquer instante — só o que caracteriza o inventário.
    /// </summary>
    private static string ConfigurationFingerprint(AppConfiguration c)
    {
        var sb = new StringBuilder();
        sb.Append((int)c.AssignmentState).Append('|').Append(c.Policies.Count).Append(';');
        foreach (var p in c.Policies
                     .OrderBy(p => (int)p.Kind)
                     .ThenBy(p => p.ExternalId, StringComparer.Ordinal))
            sb.Append((int)p.Kind).Append(':').Append(p.ExternalId).Append(':')
              .Append(p.DisplayName).Append(':').Append(p.PlatformLabel ?? "-").Append(':')
              .Append((int)p.AssignmentState).Append(':').Append(p.AssignmentCount?.ToString() ?? "-").Append(';');
        return Hash(sb);
    }

    /// <summary>SHA-256 dos DADOS de dispositivos (grupos ordenados + totais estruturais).</summary>
    private static string DeviceFingerprint(AppDevices d)
    {
        var sb = new StringBuilder();
        sb.Append(d.TotalDevices).Append('|').Append(d.StaleThresholdDays).Append('|')
          .Append(d.DevicesWithDirectoryId).Append('|').Append(d.Groups.Count).Append(';');
        foreach (var g in d.Groups
                     .OrderBy(g => g.OperatingSystem, StringComparer.Ordinal)
                     .ThenBy(g => (int)g.Compliance)
                     .ThenBy(g => (int)g.Encryption)
                     .ThenBy(g => (int)g.Activity))
            sb.Append(g.OperatingSystem).Append(':')
              .Append((int)g.Compliance).Append(',').Append((int)g.Encryption).Append(',')
              .Append((int)g.Activity).Append('=').Append(g.DeviceCount).Append(';');
        return Hash(sb);
    }

    private static string Hash(StringBuilder sb) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
}
