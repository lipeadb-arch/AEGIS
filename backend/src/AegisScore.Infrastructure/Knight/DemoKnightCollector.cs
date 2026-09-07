using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// Coletor de DEMONSTRAÇÃO do AEGIS KNIGHT: produz fatos normalizados 100% SINTÉTICOS (domínio example.com),
/// determinístico e sem rede — nunca consulta Microsoft Graph, AD local nem Okta. Percorre o MESMO pipeline
/// multicoletor das fontes reais; a única diferença é a origem dos fatos. O cenário é calibrado para o
/// resultado demonstrável preservado da Entrega 1 (5 indicadores compartilhados → score 23, cobertura 100%):
///   • 2 de 12 privilegiadas sem MFA        → AK-ENTRA-001 Exposed (Critical)
///   • 12 privilegiadas (teto 10)           → AK-ENTRA-002 Exposed (High)
///   • 0 privilegiadas com mailbox          → AK-ENTRA-003 Passed (Medium)
///   • 3 convidados inativos                → AK-ENTRA-004 Exposed (Medium)
///   • 2 contas de serviço isentas de MFA,
///     com controle compensatório COMPROVADO → AK-ENTRA-005 Mitigated (High)
/// </summary>
public sealed class DemoKnightCollector : IKnightCollector
{
    private const string DemoDomain = "demo.example.com";

    public KnightSourceType Source => KnightSourceType.Demo;

    public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
    {
        var facts = new KnightFactSet(new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 12),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, 2),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithMailbox, 0),
            KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, 3),
            KnightObservation.OfCount(KnightSignalKey.ServiceAccountsMfaExempt, 2),
            // Evidência compensatória EXPLÍCITA e comprovada — a única base admissível para Mitigated.
            KnightObservation.OfFlag(KnightSignalKey.ServiceAccountMfaExemptionProven, true),
        });

        var capabilities = new[]
        {
            new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.GuestAccounts, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.ServiceAccountExemptions, KnightCapabilityOutcome.Collected),
        };

        var result = new KnightCollectionResult(
            KnightSourceType.Demo,
            KnightSourceState.Completed,
            "Provedor de Demonstração AEGIS KNIGHT",
            facts,
            capabilities,
            DateTimeOffset.UtcNow,
            $"Cenário sintético (domínio {DemoDomain}); nunca consultou Microsoft Graph, AD local ou Okta.",
            AffectedObjects: BuildAffectedObjects());

        return Task.FromResult(result);
    }

    /// <summary>
    /// [AEGIS-MVP-PRODUCT-02] Objetos SINTÉTICOS que sustentam os três achados com detalhe nesta entrega. As
    /// listas têm exatamente o tamanho das contagens acima (12 privilegiados, 2 sem MFA, 3 convidados) — a
    /// demonstração não pode ensinar uma incoerência que a coleta real não tem. Tudo em
    /// <c>demo.example.com</c>: nenhum nome, domínio ou empresa reais.
    ///
    /// O cenário inclui de propósito um objeto SEM nome de exibição e uma identidade de APLICAÇÃO entre os
    /// privilegiados: são os dois casos que a tela precisa saber apresentar sem inventar pessoa.
    /// </summary>
    private static IReadOnlyList<KnightAffectedObjectEvidence> BuildAffectedObjects()
    {
        const string admin = "Administrador Global";
        const string helpdesk = "Administrador de Suporte";
        const string exchange = "Administrador do Exchange";

        KnightAffectedObjectFact User(int n, string nome, string papel, string? detalhe = null) =>
            new($"demo-user-{n:00}", KnightAffectedObjectKind.User, nome, $"{nome.Split(' ')[0].ToLowerInvariant()}.{n:00}@{DemoDomain}",
                new[] { papel }, detalhe ?? $"Papel(is): {papel}.");

        var privilegiados = new List<KnightAffectedObjectFact>
        {
            User(1, "Ana Prado", admin),
            User(2, "Bruno Lima", admin),
            User(3, "Carla Dias", helpdesk),
            User(4, "Diego Souza", helpdesk),
            User(5, "Elisa Faria", exchange),
            User(6, "Fabio Nunes", helpdesk),
            User(7, "Gabriela Reis", exchange),
            User(8, "Heitor Cunha", helpdesk),
            User(9, "Ines Barros", admin),
            User(10, "Joao Peixoto", helpdesk),
            // Identidade de APLICAÇÃO: aparece na contagem de objetos privilegiados e não é uma pessoa —
            // "exigir MFA" não se aplica a ela.
            new("demo-app-01", KnightAffectedObjectKind.ServicePrincipal, "Integração de inventário (demo)", null,
                new[] { helpdesk }, $"Papel(is): {helpdesk}. Identidade de aplicação — não é uma pessoa."),
            // Objeto sem nome devolvido pela fonte: identificado pelo ID, com a limitação declarada.
            new("demo-obj-12", KnightAffectedObjectKind.Unknown, null, null,
                new[] { helpdesk }, $"Papel(is): {helpdesk}. Tipo de objeto não reconhecido na resposta da fonte."),
        };

        var semMfa = new List<KnightAffectedObjectFact>
        {
            privilegiados[2] with { Detail = "Sem método capaz de MFA registrado no relatório de registro do diretório." },
            privilegiados[7] with { Detail = "Sem método capaz de MFA registrado no relatório de registro do diretório." },
        };

        var convidados = new List<KnightAffectedObjectFact>
        {
            new("demo-guest-01", KnightAffectedObjectKind.Guest, "Marina Alves (fornecedor)", $"marina.alves@parceiro.{DemoDomain}",
                null, "Último acesso registrado em 02/06/2026 — anterior à janela de 30 dias."),
            new("demo-guest-02", KnightAffectedObjectKind.Guest, "Rafael Antunes (auditoria)", $"rafael.antunes@parceiro.{DemoDomain}",
                null, "Sem registro de acesso; convite criado em 11/04/2026. Atividade desconhecida não comprova desuso."),
            new("demo-guest-03", KnightAffectedObjectKind.Guest, null, $"convidado.03@parceiro.{DemoDomain}",
                null, "Sem registro de acesso nem nome devolvido pela fonte — atividade desconhecida, não inatividade comprovada."),
        };

        return new[]
        {
            new KnightAffectedObjectEvidence(KnightSignalKey.PrivilegedAccountsTotal, privilegiados, IsComplete: true,
                Limitation: "1 objeto sem nome de exibição devolvido pela fonte — identificado pelo ID do objeto."),
            new KnightAffectedObjectEvidence(KnightSignalKey.PrivilegedAccountsWithoutMfa, semMfa, IsComplete: true),
            new KnightAffectedObjectEvidence(KnightSignalKey.InactiveGuestAccounts, convidados, IsComplete: true,
                Limitation: "2 convidado(s) sinalizado(s) por ATIVIDADE DESCONHECIDA (sem registro de acesso), " +
                            "o que não é o mesmo que inatividade comprovada."),
        };
    }
}
