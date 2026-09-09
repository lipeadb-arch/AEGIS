using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-MVP-PRE-ADM-01] Fixture da FRONTEIRA EXTERNA de coleta do Microsoft Entra ID.
///
/// Ocupa exatamente o lugar do <c>EntraIdKnightCollector</c> — o componente cuja única razão de existir é
/// falar com o Microsoft Graph. Tudo a jusante permanece REAL: a Evidence Fabric de identidade persiste o
/// snapshot, o <c>AegisKnightAssessmentService</c> avalia as regras do catálogo, o veredito é gravado e os
/// endpoints o entregam. Nada aqui simula um serviço do AEGIS.
///
/// Por que a fixture está aqui e não no transporte HTTP: o objeto deste pacote é a JORNADA (avaliação →
/// afetados → plano → execução → validação → fotografia), não o parser do Graph — que já tem cobertura
/// própria e dedicada em <c>EntraIdentityRiskCollectorTests</c>. Roteirizar os fatos permite escrever a
/// ordem temporal e a melhora observada de forma determinística, que é o que a validação precisa julgar.
///
/// ⚠️ Os dados são SINTÉTICOS (domínio <c>demo.example.com</c>). Uma execução verde aqui não afirma nada
/// sobre a integração com um tenant Microsoft real.
/// </summary>
internal sealed class ScriptedIdentityCollector : IKnightCollector
{
    private volatile Scenario _scenario = new(PrivilegedTotal: 12, WithoutMfa: 2, CollectedAt: null);

    public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;

    /// <summary>Rótulo da fonte — dito como sintético, para que nenhuma leitura o confunda com coleta real.</summary>
    public const string Label = "Diretório sintético de validação (fixture de fronteira)";

    /// <summary>
    /// Define o que a PRÓXIMA coleta vai observar. <paramref name="collectedAt"/> é obrigatório nos testes de
    /// validação: é ele que estabelece a ordem temporal entre a origem, o relato de execução e a evidência.
    /// </summary>
    public void Observe(int privilegedTotal, int withoutMfa, DateTimeOffset collectedAt) =>
        _scenario = new Scenario(privilegedTotal, withoutMfa, collectedAt);

    public Task<KnightCollectionResult> CollectAsync(
        KnightCollectionContext context, CancellationToken ct = default)
    {
        var scenario = _scenario;

        var facts = new KnightFactSet(new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, scenario.PrivilegedTotal),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, scenario.WithoutMfa),
        });

        // Os objetos preservados têm EXATAMENTE o tamanho da contagem: a comparação por conjuntos só é lícita
        // quando os dois lados estão completos, e uma lista menor que o número exibido ensinaria uma
        // incoerência que a coleta real não tem.
        var objetos = Enumerable.Range(1, scenario.WithoutMfa)
            .Select(i => new KnightAffectedObjectFact(
                $"sintetico-conta-{i:00}",
                KnightAffectedObjectKind.User,
                $"Conta sintética {i:00}",
                $"conta{i:00}@demo.example.com",
                new[] { "Administrador Global" },
                "Sem método capaz de MFA no relatório de registro do diretório sintético."))
            .ToList();

        var result = new KnightCollectionResult(
            KnightSourceType.MicrosoftEntraId,
            KnightSourceState.Completed,
            Label,
            facts,
            new[]
            {
                new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
                new KnightCapabilityStatus(KnightCapability.MfaRegistration, KnightCapabilityOutcome.Collected),
            },
            scenario.CollectedAt ?? DateTimeOffset.UtcNow,
            "Fonte SINTÉTICA de validação; nenhuma consulta ao Microsoft Graph foi feita.",
            AffectedObjects: new[]
            {
                new KnightAffectedObjectEvidence(
                    KnightSignalKey.PrivilegedAccountsWithoutMfa, objetos, IsComplete: true),
            });

        return Task.FromResult(result);
    }

    private sealed record Scenario(int PrivilegedTotal, int WithoutMfa, DateTimeOffset? CollectedAt);
}
