using AegisScore.Application.Assessment;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Motor puro de distinção telemetria × documentação. Sem banco, sem LLM — é a classificação que TODOS
/// os motores compartilham, então errar aqui erra em toda a plataforma de uma vez.
/// </summary>
public class RuleEvaluatorTests
{
    private const string EntraSource = "Entra ID: authenticationMethods e sign-in logs para MFA privilegiado";
    private const string SentinelSource = "Microsoft Sentinel: regras analíticas de autenticação sem MFA";

    [Fact]
    public void ClassificaOVocabularioRealDoCatalogo()
    {
        RuleEvaluator.NatureOf(EntraSource).Should().Be(EvidenceNature.Telemetry);
        RuleEvaluator.NatureOf(RuleEvaluator.ManualAuditToken).Should().Be(EvidenceNature.Documentation);
    }

    [Fact]
    public void ExtraiOIdentificadorAcionavelDaFonte()
    {
        // O prefixo antes do ':' é o que o operador reconhece — e o que o futuro Auditor de Conectores
        // vai agrupar para dizer "5 lacunas seriam corrigidas ligando o Entra ID".
        RuleEvaluator.SourceIdentifierOf(EntraSource).Should().Be("Entra ID");
        RuleEvaluator.SourceIdentifierOf(SentinelSource).Should().Be("Microsoft Sentinel");
        RuleEvaluator.SourceIdentifierOf(RuleEvaluator.ManualAuditToken)
            .Should().Be(RuleEvaluator.ManualAuditToken);
    }

    [Fact]
    public void FontesDeTelemetriaSaoALTERNATIVAS_ProduzemUMAlacunaSo()
    {
        // Sentinel OU SecOps OU CrowdStrike provam o mesmo controle. Emitir uma lacuna por ferramenta
        // diria ao operador que ele precisa comprar os três.
        var gaps = RuleEvaluator.Compile(
            new[] { EntraSource, SentinelSource }, hasTelemetrySignal: false, hasProcessedDocument: false);

        var gap = gaps.Should().ContainSingle().Subject;
        gap.Type.Should().Be(ComplianceRequirementType.Telemetry);
        gap.SourceIdentifier.Should().Be("Entra ID", "a primeira listada é a fonte primária");
        gap.Description.Should().Contain("Microsoft Sentinel", "as alternativas aceitas ficam visíveis");
    }

    [Fact]
    public void AsDuasNaturezasEmFalta_ProduzemUmaUnicaLacunaBoth()
    {
        var gaps = RuleEvaluator.Compile(
            new[] { EntraSource, RuleEvaluator.ManualAuditToken },
            hasTelemetrySignal: false, hasProcessedDocument: false);

        gaps.Should().ContainSingle().Which.Type.Should().Be(ComplianceRequirementType.Both,
            "fechar metade de uma pendência de dupla evidência não é progresso");
    }

    [Fact]
    public void EvidenciaPRESENTE_NaoProduzLacuna_AindaQueOControleFalhe()
    {
        // A separação que sustenta o recurso: lacuna de PROVA ≠ lacuna de PRÁTICA.
        RuleEvaluator.Compile(new[] { EntraSource }, hasTelemetrySignal: true, hasProcessedDocument: true)
            .Should().BeEmpty();
    }

    [Fact]
    public void RegraSemExigenciasNaoInventaLacuna()
    {
        RuleEvaluator.Compile(Array.Empty<string>(), false, false).Should().BeEmpty();
    }

    [Fact]
    public void SoAnaturezaEXIGIDA_ViraLacuna()
    {
        // Regra puramente documental não pode gerar lacuna de telemetria, por mais que falte sinal —
        // seria mandar o operador ligar um conector que não prova o controle.
        var gaps = RuleEvaluator.Compile(
            new[] { RuleEvaluator.ManualAuditToken }, hasTelemetrySignal: false, hasProcessedDocument: false);

        gaps.Should().ContainSingle().Which.Type.Should().Be(ComplianceRequirementType.Documentation);
    }
}
