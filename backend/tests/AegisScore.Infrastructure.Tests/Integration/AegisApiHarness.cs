using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-MVP-PRE-ADM-01] Harness de integração da APLICAÇÃO REAL: o host da API (<c>Program.cs</c>) subindo
/// sobre um PostgreSQL DESCARTÁVEL preparado pelo <c>AegisScore.DbMigrator</c> DE VERDADE.
///
/// Por que ele existe: até aqui a jornada de remediação e de fotografia era provada no nível de SERVIÇO
/// (<c>RemediationJourneyTests</c>, <c>RemediationPostgresTests</c>), e essas próprias suítes declaravam o
/// limite — "sem harness de integração HTTP o pipeline HTTP e a autenticação JWT NÃO são executados". Tudo o
/// que vive entre o cliente e o serviço ficava sem regressão: roteamento, model binding, emissão e validação
/// do JWT, <c>FallbackPolicy</c>, <c>[Authorize(Roles=...)]</c>, <c>TenantConsistencyMiddleware</c> e o Global
/// Query Filter alimentado pela claim.
///
/// O que é REAL aqui:
///  • o host da API, com o pipeline completo (o mesmo <c>Program.cs</c> que roda em produção);
///  • a preparação do banco pelo migrator real — migrations, seed do catálogo e verificação final;
///  • a autenticação: login por credencial em <c>POST /api/v1/auth/login</c>, emissão do access token pelo
///    <c>JwtTokenService</c> e validação pelo handler JwtBearer. Nenhum handler de teste "aceita qualquer
///    identidade"; o segredo de assinatura é SINTÉTICO e gerado em tempo de execução;
///  • a persistência: Npgsql sobre um database criado e destruído por execução.
///
/// O que é SUBSTITUÍDO, e só isso:
///  • os três <c>IHostedService</c> de fundo (documentos, políticas, snapshot diário) — não fazem parte da
///    jornada e tornariam o teste não determinístico;
///  • o <c>TimeProvider</c>, por um relógio controlado (ordem temporal determinística);
///  • a FRONTEIRA EXTERNA de coleta do Microsoft Entra ID: um <see cref="ScriptedIdentityCollector"/> ocupa o
///    lugar do coletor que falaria com o Microsoft Graph. Nenhum endpoint do AEGIS, nenhum serviço de
///    aplicação e nenhuma regra da jornada é simulada.
///
/// ⚠️ A fonte de dados é SINTÉTICA por construção. Isto NÃO comprova integração com um tenant Microsoft real.
/// </summary>
internal sealed class AegisApiHarness : IAsyncDisposable
{
    /// <summary>
    /// O migrator lê a connection string do AMBIENTE do processo (nunca de argumento — a recusa do
    /// <c>--connection</c> é deliberada). Variável de ambiente é estado global do processo, então a sequência
    /// "definir → executar → restaurar" precisa ser atômica entre classes de teste paralelas.
    /// </summary>
    private static readonly SemaphoreSlim MigratorGate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresProbe _pg;
    private readonly WebApplicationFactory<Program> _factory;

    private AegisApiHarness(
        PostgresProbe pg, WebApplicationFactory<Program> factory, FakeTimeProvider clock,
        ScriptedIdentityCollector entra, DateTimeOffset anchor)
    {
        _pg = pg;
        _factory = factory;
        Clock = clock;
        Entra = entra;
        Anchor = anchor;
    }

    /// <summary>Relógio dos serviços que recebem <see cref="TimeProvider"/> — avançado explicitamente.</summary>
    public FakeTimeProvider Clock { get; }

    /// <summary>Fronteira externa de coleta do Entra ID, roteirizada pelo teste.</summary>
    public ScriptedIdentityCollector Entra { get; }

    /// <summary>
    /// Origem temporal de TODAS as datas do teste. Deriva do instante real de criação (trinta dias atrás), e
    /// não de uma data literal: uma fixture com data fixa de calendário inevitavelmente alcança o presente e
    /// passa a falhar sozinha — foi exatamente o problema já corrigido em <c>RemediationPostgresTests</c>.
    /// </summary>
    public DateTimeOffset Anchor { get; }

    private DateTimeOffset _janela;

    /// <summary>
    /// Abre uma JANELA temporal exclusiva para o caso de teste que a pede, e posiciona o relógio nela.
    ///
    /// O harness é compartilhado pela classe (subir o host uma vez por caso seria caro demais), e um relógio
    /// controlado não anda para trás. Sem janelas separadas, o segundo caso tentaria voltar ao instante do
    /// primeiro. Cada janela avança um dia, de modo que os casos nunca disputam a mesma linha do tempo — e
    /// todas permanecem no PASSADO em relação ao relógio real.
    /// </summary>
    public DateTimeOffset OpenTimeWindow()
    {
        _janela = _janela == default ? Anchor : _janela.AddDays(1);
        Clock.SetUtcNow(_janela);
        return _janela;
    }

    public string ConnectionString => _pg.ConnectionString;

    public DbContextOptions<AegisScoreDbContext> DbOptions() => _pg.DbOptions();

    /// <summary>
    /// Sobe o harness completo, ou devolve <c>null</c> quando <c>AEGIS_TEST_PG</c> não está definido (a
    /// máquina de desenvolvimento não tem PostgreSQL acessível; o gate anti-falso-verde do CI continua
    /// valendo dentro do <see cref="PostgresProbe"/>).
    /// </summary>
    public static async Task<AegisApiHarness?> TryCreateAsync()
    {
        var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return null;

        try
        {
            // 1) O banco é preparado pelo MIGRATOR REAL — o mesmo binário da implantação, com migrations,
            //    seed do catálogo NIST/regras e verificação final. A API só CONSTATA a prontidão no boot.
            var exit = await RunMigratorAsync(pg.ConnectionString);
            if (exit != AegisScore.DbMigrator.MigratorExitCode.Success)
                throw new InvalidOperationException(
                    $"AegisScore.DbMigrator não preparou o banco descartável (exit={exit}). " +
                    "Sem preparação aprovada a API se recusa a subir — e é isso que ela deve fazer.");

            // 2) Segredos 100% sintéticos, existentes apenas nesta execução. A chave de assinatura é gerada
            //    agora: nenhum segredo versionado, nenhum valor previsível.
            var signingKey = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
            var anchor = new DateTimeOffset(DateTime.UtcNow.AddDays(-30).Date, TimeSpan.Zero).AddHours(9);
            var clock = new FakeTimeProvider(anchor);
            var entra = new ScriptedIdentityCollector();

            var factory = new AegisWebApplicationFactory(pg.ConnectionString, signingKey, clock, entra);

            // Força a construção do host AGORA: o bloco de arranque do Program.cs (SchemaReadinessGuard) roda
            // aqui, então uma preparação de banco incompleta falha no lugar certo, e não dentro de um teste.
            _ = factory.Services;

            return new AegisApiHarness(pg, factory, clock, entra, anchor);
        }
        catch
        {
            await pg.DisposeAsync();
            throw;
        }
    }

    // ---- Clientes HTTP -----------------------------------------------------------------------------

    /// <summary>
    /// Cliente ANÔNIMO. O cabeçalho <c>X-Forwarded-Proto</c> reproduz o proxy TLS da hospedagem (o mesmo
    /// recurso que o smoke do container usa): sem ele o <c>UseHttpsRedirection</c> — ativo fora de
    /// Development — responderia 307 a tudo, e o teste provaria o redirecionamento, não a jornada.
    /// </summary>
    public HttpClient Anonymous()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        return client;
    }

    /// <summary>Cliente com o access token REAL e o <c>X-Tenant</c> que o contrato fail-closed exige.</summary>
    public HttpClient As(SeededUser user) => As(user.AccessToken, user.TenantId);

    public HttpClient As(string accessToken, Guid tenantId)
    {
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("X-Tenant", tenantId.ToString());
        return client;
    }

    // ---- Semeadura sintética ------------------------------------------------------------------------

    /// <summary>
    /// Cria um ambiente sintético (tenant + três papéis) e AUTENTICA cada um pelo endpoint real de login.
    /// A senha existe só em memória, nesta execução.
    /// </summary>
    public async Task<SeededTenant> SeedTenantAsync(string label)
    {
        var tenantId = Guid.NewGuid();
        var slug = "t-" + tenantId.ToString("N")[..12];
        var password = "frase longa e sintetica " + Guid.NewGuid().ToString("N")[..8];

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();

            await using (var db = new AegisScoreDbContext(options, new SystemTenantContext(null)))
            {
                db.Tenants.Add(new Tenant
                {
                    Id = tenantId, Name = label, Slug = slug, Status = TenantStatus.Active,
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new AegisScoreDbContext(options, new SystemTenantContext(tenantId)))
            {
                foreach (var role in new[] { TenantRole.Analyst, TenantRole.Manager, TenantRole.TenantAdmin })
                {
                    var account = new IdentityAccount
                    {
                        Email = EmailFor(role, slug),
                        PasswordHash = hasher.Hash(password),
                        PlatformRole = PlatformRole.None,
                    };
                    db.IdentityAccounts.Add(account);
                    db.Users.Add(new User
                    {
                        TenantId = tenantId,
                        IdentityAccountId = account.Id,
                        DisplayName = $"{role} {label}",
                        Role = role,
                        IsActive = true,
                    });
                }

                await db.SaveChangesAsync();
            }
        }

        var analyst = await LoginAsync(tenantId, TenantRole.Analyst, slug, password);
        var manager = await LoginAsync(tenantId, TenantRole.Manager, slug, password);
        var admin = await LoginAsync(tenantId, TenantRole.TenantAdmin, slug, password);
        return new SeededTenant(tenantId, label, analyst, manager, admin);
    }

    private static string EmailFor(TenantRole role, string slug) =>
        $"{role.ToString().ToLowerInvariant()}.{slug}@demo.example.com";

    /// <summary>
    /// Login REAL: credencial → <c>POST /api/v1/auth/login</c> → access token assinado. Nenhum atalho de
    /// emissão direta pelo <c>IJwtTokenService</c>: é justamente a passagem pelo endpoint e pela validação do
    /// handler JwtBearer que este harness existe para provar.
    /// </summary>
    private async Task<SeededUser> LoginAsync(Guid tenantId, TenantRole role, string slug, string password)
    {
        var email = EmailFor(role, slug);
        using var client = Anonymous();
        client.DefaultRequestHeaders.Add("X-Tenant", tenantId.ToString());

        using var response = await client.PostAsync("/api/v1/auth/login", JsonBody(new { email, password }));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login sintético falhou ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var status = doc.RootElement.GetProperty("status").GetString();
        if (status != "authenticated")
            throw new InvalidOperationException($"Login sintético não emitiu sessão (status={status}).");

        var token = doc.RootElement.GetProperty("accessToken").GetString()!;
        return new SeededUser(tenantId, role, email, token);
    }

    public static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

    // ---- Migrator real ------------------------------------------------------------------------------

    /// <summary>
    /// Executa o <c>AegisScore.DbMigrator</c> de verdade contra o banco indicado e devolve o código de saída
    /// do contrato de implantação — o mesmo caminho que um job de deploy percorre.
    /// </summary>
    public static async Task<int> RunMigratorAsync(string connectionString, params string[] args)
    {
        await MigratorGate.WaitAsync();
        var previousConnection = Environment.GetEnvironmentVariable("ConnectionStrings__AegisScore");
        var previousEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousBootstrap = Environment.GetEnvironmentVariable("Bootstrap__Enabled");
        try
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__AegisScore", connectionString);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");
            // O bootstrap do primeiro administrador é etapa de INSTALAÇÃO; aqui as identidades são semeadas
            // explicitamente por papel. Desligado de forma explícita, nunca por omissão.
            Environment.SetEnvironmentVariable("Bootstrap__Enabled", "false");
            return await AegisScore.DbMigrator.Program.Main(args ?? Array.Empty<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__AegisScore", previousConnection);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousEnvironment);
            Environment.SetEnvironmentVariable("Bootstrap__Enabled", previousBootstrap);
            MigratorGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        Npgsql.NpgsqlConnection.ClearAllPools();
        await _pg.DisposeAsync();
    }

    // ---- Fábrica do host ----------------------------------------------------------------------------

    private sealed class AegisWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _signingKey;
        private readonly FakeTimeProvider _clock;
        private readonly ScriptedIdentityCollector _entra;

        public AegisWebApplicationFactory(
            string connectionString, string signingKey, FakeTimeProvider clock, ScriptedIdentityCollector entra)
        {
            _connectionString = connectionString;
            _signingKey = signingKey;
            _clock = clock;
            _entra = entra;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // "Testing" e NÃO "Development": mantém o pipeline endurecido (HSTS/HttpsRedirection ativos, sem
            // Swagger, sem as origens CORS de dev) — a configuração mais próxima da produção que um TestServer
            // permite. O Data Protection continua não exigindo certificado fora de Production.
            builder.UseEnvironment("Testing");

            builder.UseSetting("ConnectionStrings:AegisScore", _connectionString);
            builder.UseSetting("Jwt:SigningKey", _signingKey);
            builder.UseSetting("Jwt:Issuer", "aegis-score");
            builder.UseSetting("Jwt:Audience", "aegis-score");
            // Nenhuma chamada a fornecedor de IA parte deste teste; o modo simulado é declarado, não presumido.
            builder.UseSetting("Ai:Mode", "Simulated");

            builder.ConfigureTestServices(services =>
            {
                // Os workers de fundo não participam da jornada e escreveriam no banco em paralelo às
                // asserções. Removidos de forma explícita — nenhum deles é objeto deste teste.
                foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                    services.Remove(hosted);

                // Relógio controlado: o que dá ordem temporal determinística ao ciclo (execução relatada,
                // validação, reabertura) sem depender da velocidade da máquina.
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);

                // FRONTEIRA EXTERNA de coleta. O registry do KNIGHT usa "o último registrado prevalece", então
                // acrescentar aqui substitui o coletor que falaria com o Microsoft Graph — e SÓ ele. A Evidence
                // Fabric, o serviço de assessment, as regras, a persistência e os endpoints seguem reais.
                services.AddSingleton<IKnightCollector>(_entra);

                // Rate limiting por IP: no TestServer o IP remoto é nulo, então toda requisição cairia na mesma
                // partição "unknown" e a janela de login (10/min) estouraria por acúmulo entre testes — um
                // falso vermelho que não diz nada sobre o produto. Cada requisição recebe um IP distinto, ANTES
                // de qualquer middleware da aplicação; a política de rate limiting continua ativa e intacta,
                // apenas deixa de tratar o harness inteiro como um único cliente.
                services.AddSingleton<IStartupFilter, DistinctRemoteIpStartupFilter>();
            });
        }
    }

    /// <summary>Atribui um IP remoto distinto por requisição (ver o comentário no registro).</summary>
    private sealed class DistinctRemoteIpStartupFilter : IStartupFilter
    {
        private int _counter;

        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
            Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app =>
            {
                app.Use(proceed => async context =>
                {
                    var n = Interlocked.Increment(ref _counter);
                    context.Connection.RemoteIpAddress = new System.Net.IPAddress(
                        new byte[] { 10, (byte)(n >> 16), (byte)(n >> 8), (byte)n });
                    await proceed(context);
                });
                next(app);
            };
    }
}

/// <summary>Um usuário sintético autenticado de verdade — o token veio do endpoint de login.</summary>
internal sealed record SeededUser(Guid TenantId, TenantRole Role, string Email, string AccessToken);

/// <summary>Um ambiente sintético com os três papéis tenant-scoped já autenticados.</summary>
internal sealed record SeededTenant(
    Guid Id, string Label, SeededUser Analyst, SeededUser Manager, SeededUser Admin);
