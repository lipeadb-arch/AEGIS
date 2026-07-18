#if DEBUG
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using AegisScore.Api.Dev;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;

namespace AegisScore.Api.Controllers;

/// <summary>
/// ⚠️ DEBUG-ONLY — "Teste de Fogo" do motor RAG: dispara o <see cref="AegisAiEvaluatorService"/> contra
/// o motor de IA REAL (Gemini, quando <c>AegisAi:ApiKey</c> está configurada) com telemetria FORJADA,
/// e imprime o <c>ComplianceVerdict</c> no console de forma estruturada.
///
/// O que este harness prova, ponta a ponta e sem mock: catálogo NIST + <c>AegisAssessmentRule</c> (jsonb)
/// → RAG por chave (<see cref="AssessmentRuleContextBuilder"/>) → System/User Prompt → Gemini →
/// parsing do bloco <c>intelligence</c> → <see cref="ControlStateWriter"/> → ledger.
///
/// ⚠️ **NÃO é um teste automatizado**: não afirma nada sozinho. A saída traz a EXPECTATIVA de cada
/// cenário ao lado do veredito real, para conferência humana — um LLM não é determinístico e transformar
/// isso num assert verde/vermelho criaria um teste instável (flaky) que mentiria nas duas direções.
///
/// ⚠️ **ESCREVE NO LEDGER** do tenant demo (o avaliador persiste por design). É a prova de que o caminho
/// completo funciona — e de quebra enche o `/scoring/dashboard` com inteligência REAL do Gemini.
/// </summary>
[ApiController]
[Route("api/v1/dev/rag-fire-test")]
[AllowAnonymous] // utilitário de bancada (DEBUG), como o resto do DevController — não exige JWT
public sealed class DevRagFireTestController : ControllerBase
{
    private readonly DbContextOptions<AegisScoreDbContext> _dbOptions;
    private readonly ILLMClient _llm;
    private readonly IOptions<AegisAiOptions> _aiOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DevRagFireTestController> _log;
    private readonly IWebHostEnvironment _env;

    public DevRagFireTestController(
        DbContextOptions<AegisScoreDbContext> dbOptions,
        ILLMClient llm,
        IOptions<AegisAiOptions> aiOptions,
        ILoggerFactory loggerFactory,
        ILogger<DevRagFireTestController> log,
        IWebHostEnvironment env)
    {
        _dbOptions = dbOptions;
        _llm = llm;
        _aiOptions = aiOptions;
        _loggerFactory = loggerFactory;
        _log = log;
        _env = env;
    }

    /// <summary>Lista os cenários disponíveis e qual motor está cabeado (Gemini real vs Stub).</summary>
    [HttpGet]
    public IActionResult Describe()
    {
        if (!_env.IsDevelopment()) return NotFound();

        return Ok(new
        {
            engine = EngineName(),
            model = _aiOptions.Value.Model,
            apiKeyConfigured = !string.IsNullOrWhiteSpace(_aiOptions.Value.ApiKey),
            tenant = DevController.DemoTenantId,
            scenarios = RagFireTestScenarios.All.Values.Select(s => new
            {
                s.Key, s.Title, s.DefaultControl, s.Expectation,
            }),
            usage = "POST /api/v1/dev/rag-fire-test?scenario=all  (ou scenario=A|B|C|D&control=PR.DS-01)",
        });
    }

    /// <summary>
    /// Dispara um cenário (ou todos) contra o motor de IA e devolve os vereditos. O relatório bonito vai
    /// para o CONSOLE da API (ILogger); a resposta HTTP traz os mesmos dados em JSON.
    /// </summary>
    /// <param name="scenario">"A".."D" ou "all" (padrão).</param>
    /// <param name="control">Sobrescreve o controle NIST do cenário (para apontar o mesmo payload a outra regra).</param>
    [HttpPost]
    public async Task<IActionResult> Fire(
        [FromQuery] string scenario = "all",
        [FromQuery] string? control = null,
        CancellationToken ct = default)
    {
        if (!_env.IsDevelopment()) return NotFound();

        var selected = ResolveScenarios(scenario);
        if (selected.Count == 0)
            return BadRequest(new { error = $"Cenário '{scenario}' desconhecido.", valid = RagFireTestScenarios.All.Keys });

        // Contexto de tenant NÃO-HTTP: o stamping é fail-closed e este endpoint é anônimo, então o
        // tenant precisa ser resolvido explicitamente — mesmo idioma do seeder no DevController.
        var tenantId = DevController.DemoTenantId;
        await using var db = new AegisScoreDbContext(_dbOptions, new FireTestTenantContext(tenantId));

        if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId, ct))
            return BadRequest(new { error = "Tenant demo ausente. Rode POST /api/v1/dev/seed-demo primeiro." });

        // O avaliador é montado À MÃO (não resolvido do DI) porque suas dependências scoped estão presas
        // ao DbContext da REQUISIÇÃO, cujo tenant HTTP é nulo aqui (endpoint anônimo). O ILLMClient, esse
        // sim, vem do DI — é ele que carrega o Gemini real e é o ponto do teste.
        var writer = new ControlStateWriter(db, new FireTestTenantContext(tenantId), _loggerFactory.CreateLogger<ControlStateWriter>());
        var evaluator = new AegisAiEvaluatorService(
            db, _llm, new FireTestTenantContext(tenantId), writer, new AssessmentRuleContextBuilder(db));

        var results = new List<object>();
        var report = new StringBuilder();
        report.AppendLine();
        report.AppendLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        report.AppendLine($"║  AEGIS SCORE · TESTE DE FOGO DO MOTOR RAG                                    ║");
        report.AppendLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        report.AppendLine($"  Motor .......: {EngineName()} ({_aiOptions.Value.Model})");
        report.AppendLine($"  Tenant ......: {tenantId}");
        report.AppendLine($"  Cenários ....: {string.Join(", ", selected.Select(s => s.Key))}");

        foreach (var sc in selected)
        {
            var code = control ?? sc.DefaultControl;
            var payload = sc.BuildPayload();

            // A regra é lida ANTES para o relatório provar que o RAG achou (ou não) a linha jsonb —
            // é a diferença entre "o LLM chutou" e "o LLM aplicou a nossa regra".
            var rule = await new AssessmentRuleContextBuilder(db).BuildAsync(code, ct);

            report.AppendLine();
            report.AppendLine("──────────────────────────────────────────────────────────────────────────────");
            report.AppendLine($"  CENÁRIO {sc.Key} · {sc.Title}");
            report.AppendLine($"  Controle ....: {code}");
            report.AppendLine($"  Regra (RAG) .: {(rule is null ? "⚠️ NENHUMA regra jsonb para este código — o prompt cai na definição pura" : $"✓ carregada ({rule.Length} chars)")}");
            report.AppendLine($"  Payload .....: {payload.Length} chars de telemetria forjada");
            report.AppendLine($"  Esperado ....: {sc.Expectation}");

            var sw = Stopwatch.StartNew();
            try
            {
                var verdict = await evaluator.EvaluateAsync(tenantId, code, payload, ct);
                sw.Stop();

                AppendVerdict(report, verdict, sw.ElapsedMilliseconds);
                results.Add(new
                {
                    scenario = sc.Key, control = code, expectation = sc.Expectation,
                    ruleLoaded = rule is not null, elapsedMs = sw.ElapsedMilliseconds,
                    status = verdict.Status.ToString(),
                    aiEvidence = verdict.AiEvidence,
                    intelligence = verdict.Intelligence,
                    checks = verdict.Checks,
                });
            }
            catch (Exception ex) when (ex is AiUnavailableException or InvalidOperationException)
            {
                sw.Stop();
                report.AppendLine($"  ✗ FALHA ({sw.ElapsedMilliseconds} ms): {ex.Message}");
                results.Add(new
                {
                    scenario = sc.Key, control = code, expectation = sc.Expectation,
                    ruleLoaded = rule is not null, elapsedMs = sw.ElapsedMilliseconds,
                    error = ex.Message,
                });
            }
        }

        report.AppendLine();
        report.AppendLine("══════════════════════════════════════════════════════════════════════════════");
        report.AppendLine("  ⚠️ Confira os vereditos contra a coluna 'Esperado'. Sem asserção automática:");
        report.AppendLine("     um LLM não é determinístico e um assert aqui seria um teste instável.");
        report.AppendLine();

        _log.LogInformation("{FireTestReport}", report.ToString());

        return Ok(new { engine = EngineName(), model = _aiOptions.Value.Model, tenant = tenantId, results });
    }

    /// <summary>Bloco de veredito no relatório — o "bonito e estruturado" pedido no harness.</summary>
    private static void AppendVerdict(StringBuilder report, ComplianceVerdict v, long elapsedMs)
    {
        var icon = v.Status switch
        {
            ControlStatus.Compliant => "✓",
            ControlStatus.MitigatedByThirdParty => "◐",
            _ => "✗",
        };

        report.AppendLine($"  ── VEREDITO ({elapsedMs} ms) ──");
        report.AppendLine($"  {icon} Status ....: {v.Status}");
        report.AppendLine($"    Pontuação .: {v.AwardedScore}/{v.MaxScorePoints}");
        report.AppendLine($"    Evidência .: {Wrap(v.AiEvidence, 60, "                 ")}");

        if (v.Intelligence is { } intel)
        {
            report.AppendLine($"    Severidade : {intel.Severity?.ToString() ?? "—"}");
            report.AppendLine($"    Confiança .: {(intel.AiConfidenceScore is { } c ? $"{c:0}%" : "—")}");
            report.AppendLine($"    Ameaças ...: {(intel.ThreatLandscape is { Count: > 0 } t ? string.Join(" · ", t) : "—")}");
            report.AppendLine($"    Remediação : {Wrap(intel.RemediationPlan ?? "—", 60, "                 ")}");
        }
        else
        {
            report.AppendLine("    Inteligência: ⚠️ AUSENTE — o motor não emitiu o bloco 'intelligence'.");
        }

        if (v.Checks is { Count: > 0 })
        {
            report.AppendLine("    Checklist .:");
            foreach (var chk in v.Checks)
                report.AppendLine($"      {(chk.Passed ? "✓" : "✕")} {chk.Name} — {chk.Details}");
        }
    }

    /// <summary>Quebra texto longo em linhas alinhadas, para o console não virar uma parede.</summary>
    private static string Wrap(string text, int width, string indent)
    {
        if (string.IsNullOrWhiteSpace(text)) return "—";

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        var lineLen = 0;

        foreach (var w in words)
        {
            if (lineLen > 0 && lineLen + w.Length + 1 > width)
            {
                sb.AppendLine();
                sb.Append(indent);
                lineLen = 0;
            }
            if (lineLen > 0) { sb.Append(' '); lineLen++; }
            sb.Append(w);
            lineLen += w.Length;
        }
        return sb.ToString();
    }

    private List<FireTestScenario> ResolveScenarios(string scenario) =>
        scenario.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? RagFireTestScenarios.All.Values.ToList()
            : RagFireTestScenarios.All.TryGetValue(scenario, out var one) ? [one] : [];

    private string EngineName() =>
        _llm is GeminiLlmClient ? "Gemini (REAL)" : $"{_llm.GetType().Name} (⚠️ NÃO é o motor real)";

    /// <summary>Contexto de tenant privilegiado, não-HTTP — espelha o SystemTenantContext do DevController.</summary>
    private sealed class FireTestTenantContext : ITenantContext
    {
        public FireTestTenantContext(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; }
    }
}
#endif
