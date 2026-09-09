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

    /// <summary>
    /// Quantas vezes a FRONTEIRA externa foi efetivamente exercida. É o número que prova "uma aquisição
    /// lógica = uma coleta" e que abrir telas/relatórios não dispara consulta nova ao diretório.
    /// </summary>
    public int Calls => _calls;

    private int _calls;

    public Task<KnightCollectionResult> CollectAsync(
        KnightCollectionContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        var scenario = _scenario;

        var facts = new KnightFactSet(new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, scenario.PrivilegedTotal),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, scenario.WithoutMfa),
        });

        // Os objetos preservados têm EXATAMENTE o tamanho da contagem: a comparação por conjuntos só é lícita
        // quando os dois lados estão completos, e uma lista menor que o número exibido ensinaria uma
        // incoerência que a coleta real não tem.
        //
        // [AEGIS-ADM-01] Os dois conjuntos são emitidos, e os sinalizados são um SUBCONJUNTO dos
        // privilegiados — os mesmos identificadores. É a forma real do diretório: a conta que aparece sem
        // método capaz de MFA registrado é uma das que têm papel privilegiado, e não um objeto à parte.
        var privilegiados = Enumerable.Range(1, scenario.PrivilegedTotal).Select(Conta).ToList();
        var semMfa = privilegiados.Take(scenario.WithoutMfa)
            .Select(o => o with
            {
                Detail = "Sem método capaz de MFA no relatório de registro do diretório sintético.",
            })
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
                    KnightSignalKey.PrivilegedAccountsTotal, privilegiados, IsComplete: true),
                new KnightAffectedObjectEvidence(
                    KnightSignalKey.PrivilegedAccountsWithoutMfa, semMfa, IsComplete: true),
            });

        return Task.FromResult(result);
    }

    /// <summary>
    /// Identificador ESTÁVEL entre coletas — é ele, e não o nome, que faz duas observações apontarem para a
    /// mesma identidade canônica. O nome existe só para a tela do analista.
    /// </summary>
    public static string ExternalIdOf(int i) => $"sintetico-conta-{i:00}";

    private static KnightAffectedObjectFact Conta(int i) => new(
        ExternalIdOf(i),
        KnightAffectedObjectKind.User,
        $"Conta sintética {i:00}",
        $"conta{i:00}@demo.example.com",
        new[] { "Administrador Global" },
        "Objeto com papel privilegiado no diretório sintético.");

    private sealed record Scenario(int PrivilegedTotal, int WithoutMfa, DateTimeOffset? CollectedAt);
}
