using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-SPA-DEEPLINK-01] Abertura direta e recarga das rotas do SPA servido pela PRÓPRIA API (o mesmo
/// <c>Program.cs</c> da imagem), sobre um <c>wwwroot</c> sintético com o shell e um asset.
///
/// O defeito: o fallback genérico <c>MapFallbackToFile("index.html")</c> carrega a restrição implícita
/// <c>nonfile</c>. O ponto dos códigos NIST (<c>/nist/rc/RC.RP-06</c>) faz o último segmento parecer arquivo,
/// nenhum endpoint casa e a <c>FallbackPolicy</c> responde 401 antes de o Angular carregar.
///
/// O que estes casos fixam: as URLs de subcategoria entregam o shell (GET e HEAD, com consulta, sem
/// redirecionamento); a regra NÃO vira um fallback anônimo para qualquer caminho com ponto — API, health,
/// função inexistente, código fora do formato e arquivo inexistente continuam fora dela; assets existentes e as
/// demais rotas do SPA seguem como antes.
/// </summary>
public sealed class SpaDeepLinkHttpTests : IClassFixture<SpaDeepLinkHttpTests.Fixture>
{
    private const string ShellMarker = "<!-- aegis-spa-shell-sintetico -->";
    private const string AssetBody = "/* asset-sintetico */";

    private readonly AegisApiHarness? _api;

    public SpaDeepLinkHttpTests(Fixture fixture) => _api = fixture.Api;

    public static TheoryData<string> SubcategoryUrls => new()
    {
        "/nist/rc/RC.RP-06",
        "/nist/rc/RC.CO-03",
        "/nist/gv/GV.OC-01",
        "/nist/id/ID.AM-01",
        "/nist/pr/PR.AA-05",
        "/nist/de/DE.CM-01",
        "/nist/rs/RS.MA-02",
        // O componente normaliza a caixa do código e da função; a URL digitada em minúsculas também abre.
        "/nist/rc/rc.rp-06",
        "/NIST/RC/RC.RP-06",
        // Contexto de avaliação, rodada e escopo viaja na consulta e precisa sobreviver à recarga.
        "/nist/rc/RC.RP-06?avaliacao=3f2b7c1e-0000-4000-8000-000000000001&rodada=2&escopo=tenant",
    };

    [Theory]
    [MemberData(nameof(SubcategoryUrls))]
    public async Task Subcategoria_GetSemSessao_EntregaOShellSemRedirecionar(string url)
    {
        if (_api is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        using var client = _api.Anonymous();

        using var r = await client.GetAsync(url);

        r.StatusCode.Should().Be(HttpStatusCode.OK, $"{url} é rota do Angular, não recurso protegido");
        r.Headers.Location.Should().BeNull("a URL (com a consulta) precisa ficar como foi pedida");
        r.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await r.Content.ReadAsStringAsync()).Should().Contain(ShellMarker);
    }

    [Theory]
    [MemberData(nameof(SubcategoryUrls))]
    public async Task Subcategoria_Head_RespondeComoOGet(string url)
    {
        if (_api is null) return;
        using var client = _api.Anonymous();

        using var r = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Subcategoria_ComSessao_TambemEntregaOShell()
    {
        if (_api is null) return;
        var t = await _api.SeedTenantAsync("Deep link NIST");
        using var client = _api.As(t.Analyst);

        using var r = await client.GetAsync("/nist/rc/RC.RP-06?rodada=1");

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).Should().Contain(ShellMarker);
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/history")]
    [InlineData("/nist")]
    [InlineData("/nist/rc")]
    [InlineData("/nist/gv/documentos")]
    [InlineData("/knight")]
    public async Task DemaisRotasDoSpa_SeguemNoFallbackGenerico(string url)
    {
        if (_api is null) return;
        using var client = _api.Anonymous();

        using var r = await client.GetAsync(url);

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).Should().Contain(ShellMarker);
    }

    [Fact]
    public async Task AssetExistente_SegueServidoPeloArquivoEstatico()
    {
        if (_api is null) return;
        using var client = _api.Anonymous();

        using var r = await client.GetAsync("/main-SINTETICO.js");

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
        (await r.Content.ReadAsStringAsync()).Should().Be(AssetBody);
    }

    /// <summary>
    /// Caminhos que a nova regra NÃO pode capturar: a resposta continua sendo a da autenticação (401), nunca o
    /// shell. É o que impede o fallback de subcategoria de virar um "qualquer coisa com ponto devolve index.html".
    /// </summary>
    [Theory]
    [InlineData("/api/v1/knight/assessments/latest")]
    [InlineData("/api/v1/nist/rc/RC.RP-06")]
    [InlineData("/api/nist/rc/RC.RP-06")]
    [InlineData("/nist/xx/RC.RP-06")]            // função inexistente
    [InlineData("/nist/rc/RC.RP-06.js")]         // arquivo, não código
    [InlineData("/nist/rc/RC.RP-6")]             // fora do formato CSF 2.0
    [InlineData("/nist/rc/inexistente.map")]
    [InlineData("/nao-existe.js")]
    public async Task ForaDoFormato_NaoViraShell_ESegueProtegido(string url)
    {
        if (_api is null) return;
        using var client = _api.Anonymous();

        using var r = await client.GetAsync(url);

        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{url} não é rota de subcategoria");
        (await r.Content.ReadAsStringAsync()).Should().NotContain(ShellMarker);
    }

    [Fact]
    public async Task ApiProtegida_ComSessao_SegueRespondendoAApi()
    {
        if (_api is null) return;
        var t = await _api.SeedTenantAsync("Deep link API");
        using var client = _api.As(t.Analyst);

        using var r = await client.GetAsync("/api/v1/knight/assessments/latest");

        r.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Health_SegueAnonimoENaoViraShell()
    {
        if (_api is null) return;
        using var client = _api.Anonymous();

        using var r = await client.GetAsync("/health/live");

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await r.Content.ReadAsStringAsync()).Should().NotContain(ShellMarker);
    }

    /// <summary>Host da API servindo um <c>wwwroot</c> sintético descartável (shell + um asset).</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private readonly string _webRoot =
            Path.Combine(Path.GetTempPath(), "aegis-spa-deeplink-" + Guid.NewGuid().ToString("N")[..12]);

        internal AegisApiHarness? Api { get; private set; }

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(_webRoot);
            await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"),
                $"<!doctype html><html><head><base href=\"/\"></head><body><app-root></app-root>{ShellMarker}</body></html>");
            await File.WriteAllTextAsync(Path.Combine(_webRoot, "main-SINTETICO.js"), AssetBody);
            Api = await AegisApiHarness.TryCreateAsync(_webRoot);
        }

        public async Task DisposeAsync()
        {
            if (Api is not null) await Api.DisposeAsync();
            try { Directory.Delete(_webRoot, recursive: true); } catch (IOException) { }
        }
    }
}
