using AegisScore.Application.Services;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Camada de PERSONALIDADE do Auditor Virtual. O risco que estes testes cobrem é específico da
/// arquitetura escolhida: a persona vive num JSON editável sem recompilar, então um erro de digitação
/// ou uma chave renomeada NÃO quebra o build — degrada o Auditor em silêncio, e um veredito escrito em
/// siglas passa despercebido até alguém ler o painel. O caso do arquivo REAL é a rede de segurança.
/// </summary>
public class AuditorPersonaTests
{
    private static string RealPersonalityPath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "AuditorPersonality.json");

    [Fact]
    public void RealPersonalityFile_LoadsWithEveryBlockPopulated()
    {
        var provider = new AuditorPersonaProvider(
            RealPersonalityPath, NullLogger<AuditorPersonaProvider>.Instance);

        var persona = provider.Persona;

        persona.IsEmpty.Should().BeFalse("o arquivo de personalidade de produção precisa carregar");
        persona.Persona.Should().NotBeNullOrWhiteSpace();
        persona.Tone.Should().NotBeEmpty();
        persona.ActionDirectives.Should().NotBeEmpty();
        // O glossário é o coração da Regra de Ouro: sem ele o Auditor volta a ecoar sigla.
        persona.TranslationRules.Should().NotBeEmpty();
        persona.TranslationRules.Should().OnlyContain(
            r => !string.IsNullOrWhiteSpace(r.Code) && !string.IsNullOrWhiteSpace(r.BusinessTerm));
        // RS.MA é o exemplo canônico da diretriz (MTTA/MTTR) — se sumir, a tradução perdeu o caso-teste.
        persona.TranslationRules.Should().Contain(r => r.Code == "RS.MA");
    }

    [Fact]
    public void PromptBlock_RendersRoleGlossaryAndDirectives()
    {
        var persona = new AuditorPersona(
            "Consultor Estratégico",
            new[] { "Proativo", "Didático" },
            new[] { new AuditorTranslationRule("RS.MA", "Resposta a Incidentes (MTTA/MTTR)") },
            new[] { "Sugira a correção antes do pedido." });

        var block = persona.ToPromptBlock();

        block.Should().Contain("ROLE: Consultor Estratégico");
        block.Should().Contain("Proativo · Didático");
        block.Should().Contain("RS.MA → Resposta a Incidentes (MTTA/MTTR)");
        block.Should().Contain("Sugira a correção antes do pedido.");
        // Invariante de GRC: a persona governa redação, JAMAIS o veredito. O aviso viaja no próprio bloco.
        block.Should().Contain("NEVER changes the status");
    }

    [Fact]
    public void NeutralPersona_RendersNothing()
    {
        // Persona vazia tem de sair do prompt por inteiro — um cabeçalho "PERSONA" sem conteúdo só
        // gastaria tokens e diluiria a atenção do modelo nas regras que importam.
        AuditorPersona.Neutral.ToPromptBlock().Should().BeEmpty();
    }

    [Fact]
    public void MissingFile_FallsBackToNeutral_WithoutThrowing()
    {
        // Fail-soft deliberado (≠ FrameworkSeeder, que aborta o boot): sem persona o veredito é
        // idêntico, só a redação fica seca. Derrubar a API por um arquivo de TOM seria desproporcional.
        var provider = new AuditorPersonaProvider(
            Path.Combine(AppContext.BaseDirectory, "Data", "nao-existe.json"),
            NullLogger<AuditorPersonaProvider>.Instance);

        provider.Persona.Should().Be(AuditorPersona.Neutral);
    }

    [Fact]
    public void MalformedFile_FallsBackToNeutral_WithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aegis-persona-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ isto não é json válido ");
        try
        {
            var provider = new AuditorPersonaProvider(path, NullLogger<AuditorPersonaProvider>.Instance);
            provider.Persona.Should().Be(AuditorPersona.Neutral);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
