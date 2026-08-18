using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using AegisScore.Api;
using AegisScore.Api.Auth;
using AegisScore.Api.Health;
using AegisScore.Api.Workers;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Microsoft;
using AegisScore.Connectors.Google;
using AegisScore.Infrastructure;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.DataProtection;
using AegisScore.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// [Homologação em container] Respeita a porta fornecida pela hospedagem (Render/Heroku expõem $PORT).
// Sem PORT (desenvolvimento local), o binding padrão de launchSettings/ASPNETCORE_URLS segue valendo.
var listenPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(listenPort))
    builder.WebHost.UseUrls($"http://0.0.0.0:{listenPort}");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Aegis Score API", Version = "v1" });

    // Permite testar endpoints protegidos pelo Swagger UI colando o access token.
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Cole apenas o access token (o prefixo 'Bearer ' é adicionado automaticamente).",
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            },
            Array.Empty<string>()
        },
    });
});

// Per-request tenant resolution (X-Tenant header) feeds the DbContext query filters.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

// Persistence + AI engine + connector registry + scoring services (registra também IAuthService/JWT).
builder.Services.AddAegisScoreInfrastructure(builder.Configuration);

// Autenticação JWT (Bearer). Habilita a validação de access tokens sem torná-la obrigatória:
// nenhum endpoint existente ganha [Authorize] aqui, então o fluxo atual segue intacto. Aplicar
// [Authorize] aos controllers é o próximo passo (fora do escopo desta etapa).
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
    throw new InvalidOperationException(
        "Jwt:SigningKey ausente ou fraca (mínimo 32 bytes para HS256). " +
        "Defina um segredo forte via user-secrets em dev ou env var/Key Vault em produção.");

// [AEGIS-AUD-007] Federação corporativa (Entra ID). Fail-fast ANTES de servir: em Federated/Hybrid a
// config obrigatória é validada aqui; em Local é no-op (dev/demonstração seguem sem federação).
var federation = builder.Configuration.GetSection(FederationOptions.SectionName).Get<FederationOptions>()
    ?? new FederationOptions();
federation.Validate();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    // Esquema PADRÃO: o JWT LOCAL do AEGIS (HS256). É o que a FallbackPolicy e todo [Authorize] usam —
    // a federação NÃO o substitui.
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;   // preserva 'sub' e 'tenant_id' como emitidos
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            // [Baixo] Fixa o algoritmo aceito em HS256 — barra confusão de algoritmo (alg=none / RS↔HS).
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            // [Alto 3] Sem isto, [Authorize(Roles=...)] procuraria ClaimTypes.Role e falharia:
            // a claim é emitida como "role" e MapInboundClaims=false a preserva com esse nome.
            RoleClaimType = "role",
        };
    })
    // [AEGIS-AUD-007] Esquema SEPARADO que valida tokens do Entra (assinatura via JWKS do tenant, issuer,
    // audience, lifetime). SÓ o endpoint /auth/federation/exchange o usa. Em modo Local ele rejeita tudo,
    // sem rede — a troca fica indisponível.
    .AddJwtBearer(FederatedAuthDefaults.Scheme, options =>
    {
        options.MapInboundClaims = false;   // preserva tid/oid/preferred_username
        if (federation.FederationEnabled)
        {
            options.Authority = federation.Authority;   // busca OIDC metadata + JWKS do tenant
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = federation.ValidIssuers,
                ValidateAudience = true,
                ValidAudiences = federation.ValidAudiences,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                // [AEGIS-AUD-007] Fixa o algoritmo ASSIMÉTRICO do Entra (RS256) — barra confusão de
                // algoritmo (alg=none, ou HS256 forjado com a chave pública do JWKS como segredo).
                ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
            };
        }
        else
        {
            // Local: nenhuma Authority (sem rede) e nenhuma chave de assinatura → toda validação falha.
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = Array.Empty<SecurityKey>(),
            };
        }
    });
builder.Services.AddAuthorization(options =>
{
    // Secure-by-default: todo endpoint exige usuário autenticado, exceto os marcados com
    // [AllowAnonymous] (AuthController e, apenas em DEBUG, DevController). Qualquer controller novo
    // já nasce protegido, sem depender de o autor lembrar de anotar [Authorize].
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // [AEGIS-AUD-007] Policy da troca federada: autenticada EXCLUSIVAMENTE pelo esquema EntraId, mais o
    // requisito de token delegado do SPA (scope/azp/tid/oid) do FederatedExchangeHandler.
    options.AddPolicy(FederatedExchangeRequirement.PolicyName, p => p
        .AddAuthenticationSchemes(FederatedAuthDefaults.Scheme)
        .RequireAuthenticatedUser()
        .AddRequirements(new FederatedExchangeRequirement()));

    // [AEGIS-AUD-011] Policy de administração de PLATAFORMA: exige a claim global platform_role=PlatformAdmin
    // (nunca derivada de User.Role). Substitui [Authorize(Roles="PlatformAdmin")] nas rotas de plataforma.
    PlatformAuthorization.AddPlatformPolicy(options);
});
// Handler da policy da troca federada (lê FederationOptions).
builder.Services.AddSingleton<IAuthorizationHandler, FederatedExchangeHandler>();

// Stack adapters (add AWS/SIEM/EDR connector packages here).
builder.Services.AddMicrosoftConnectors();
builder.Services.AddGoogleConnectors();

// Document Hub: worker que lê os documentos enfileirados e mapeia os controles NIST.
builder.Services.AddHostedService<DocumentAnalysisWorker>();

// Govern: worker que PUXA políticas das fontes externas (SharePoint/Google…) de forma agnóstica via
// Provider Pattern e as injeta no hub — que o DocumentAnalysisWorker acima então lê. Fetch, não análise.
builder.Services.AddHostedService<PolicyIngestionWorker>();

// Aegis Score: worker que grava a foto agregada diária por tenant (série do gráfico de tendência).
builder.Services.AddHostedService<AegisScoreSnapshotWorker>();

// [Homologação em container] Atrás do proxy HTTPS da hospedagem: honra X-Forwarded-Proto/For para que
// Request.Scheme reflita https (cookie Secure do refresh e HttpsRedirection corretos) e o IP do cliente
// seja o real (rate limiting por IP). O proxy da hospedagem tem IP dinâmico e desconhecido de antemão;
// sem limpar estas listas o middleware descartaria os cabeçalhos. A borda pública é o proxy, não a app.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

const string SpaCors = "aegis-spa";
// [Homologação em container] Allowlist CONFIGURÁVEL (Cors:AllowedOrigins). O caminho principal de produção
// é SAME-ORIGIN — o SPA é servido pela própria API —, então a lista pode ficar vazia em produção (CORS não
// se aplica a same-origin). As origens de localhost do ng serve entram SOMENTE em Development. NUNCA se usa
// AllowAnyOrigin com credenciais (proibido pelo navegador e inseguro).
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
if (builder.Environment.IsDevelopment())
    corsOrigins = corsOrigins
        .Concat(new[] { "http://localhost:5173", "http://localhost:5273", "http://localhost:3000" })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
builder.Services.AddCors(o => o.AddPolicy(SpaCors, p => p
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    // [AEGIS-AUD-009] AllowAnyHeader libera os headers de REQUISIÇÃO; para o SPA conseguir LER um header
    // de RESPOSTA cross-origin ele precisa ser explicitamente exposto. Sem isto o Angular não enxerga o
    // Retry-After do 409 de conflito benigno de rotação (front e API em portas distintas em dev).
    // [AEGIS-AUD-034] Content-Disposition é exposto para o download PDF/CSV usar o filename do servidor.
    .WithExposedHeaders("Retry-After", "Content-Disposition")
    .AllowCredentials()));   // necessário para o SPA enviar/receber o cookie HttpOnly de refresh

// [AEGIS-AUD-053] Data Protection: provê IDataProtectionProvider para cifrar segredos de conector.
// Key ring persistido no PostgreSQL compartilhado (sobrevive a restart e é o MESMO entre réplicas),
// application discriminator estável por ambiente e envelope das chaves em repouso — obrigatório em
// Production. Toda a política e o fail-fast vivem em DataProtectionPlan. Ver ConnectorSecretProtector.
builder.Services.AddAegisDataProtection(builder.Configuration, builder.Environment);

// [Alto 4] Rate limiting nativo do .NET 10: blinda a superfície anônima (login/refresh) contra brute
// force, credential stuffing (X-Tenant é spoofável) e o DoS por replay da cascata de breach.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Login: janela apertada por IP (freia brute force / stuffing).
    o.AddPolicy("auth-login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientIp(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // Refresh: mais folgado (uso legítimo é frequente), mas ainda corta o replay em massa.
    o.AddPolicy("auth-refresh", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientIp(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // Troca da PRÓPRIA senha (autenticada): verifica a senha ATUAL, então é alvo de brute force apesar de
    // exigir sessão. Janela apertada por IP, no mesmo idioma do login — nunca ilimitada.
    o.AddPolicy("auth-password", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientIp(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // [AEGIS-AUD-020] Ingestão externa de eventos (SIEM/EDR push): proporcional a um emissor legítimo, que
    // envia lotes com frequência de egress fixo. Particionado por IP (limita o nº de partições) — blinda o
    // endpoint anônimo contra flood, sem sufocar a coleta real.
    o.AddPolicy("ingestion", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientIp(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // Auditor Virtual (chat fundamentado): limite de perguntas por usuário/minuto — controle do Free Tier
    // (Ai:FreeTier:MaxQuestionsPerMinute), protege a cota do provedor gratuito. Particionado pela identidade
    // autenticada (claim), caindo para o IP quando anônimo.
    var auditorPerMinute = Math.Max(1, builder.Configuration.GetValue<int?>("Ai:FreeTier:MaxQuestionsPerMinute") ?? 10);
    o.AddPolicy("ai-auditor", ctx => RateLimitPartition.GetFixedWindowLimiter(
        AuthenticatedPartition(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = auditorPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    static string AuthenticatedPartition(HttpContext ctx) =>
        ctx.User?.FindFirst("account_id")?.Value
        ?? ctx.User?.FindFirst("sub")?.Value
        ?? (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
});

// [AEGIS-AUD-048] Health checks REAIS, separando dois conceitos distintos:
//  - "self" (liveness): confirma só que o PROCESSO está vivo. Não toca banco, IA, SIEM/EDR ou fornecedor
//    externo — é o que um orquestrador usa para decidir reiniciar um processo travado.
//  - "readiness": confirma que a aplicação está APTA a servir (PostgreSQL + migrations + catálogo íntegro),
//    reusando o SchemaReadinessGuard. Só leitura; a ausência de Azure OpenAI/Entra/conectores NÃO reprova.
// Os endpoints (/health/live e /health/ready) são mapeados abaixo, anônimos e com resposta sanitizada.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<AegisReadinessHealthCheck>("readiness", tags: new[] { "ready" });

var app = builder.Build();

// [AEGIS-AUD-052] Prontidão do banco: CONSTATAR, nunca mutar.
//
// A API não aplica mais migrations nem semeia o catálogo. Toda réplica fazia isso no boot, e o seed
// rodava FORA do lock que o EF Core adquire em MigrateAsync() — duas réplicas subindo juntas podiam
// inserir dois catálogos completos (nenhum índice o impedia até esta entrega), e o boot passava a
// falhar para sempre. Agora a preparação do banco é etapa própria de implantação, executada pelo
// AegisScore.DbMigrator sob advisory lock.
//
// Este bloco roda ANTES de app.Run(): builder.Build() apenas CONSTRÓI o host — os hosted services só
// são iniciados por Run(). Logo, se a verificação reprovar, nenhum worker chega a processar trabalho.
// Falha em TODOS os ambientes, inclusive Development: subir sobre um catálogo ausente ou duplicado
// significa reportar postura de segurança falsa, que é pior do que não subir.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AegisScoreDbContext>();
        // Ausente apenas com key ring efêmero (configuração de teste).
        var keyRingDb = scope.ServiceProvider.GetService<DataProtectionKeyDbContext>();

        await SchemaReadinessGuard.EnsureReadyAsync(db, keyRingDb);

        logger.LogInformation(
            "Startup: banco verificado — migrations aplicadas nos dois contextos, catálogo NIST CSF 2.0 " +
            "e regras de avaliação presentes e íntegros.");
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex,
            "Startup: banco não está preparado para esta versão da API. Abortando o boot. " +
            "Execute o AegisScore.DbMigrator como etapa de implantação.");
        throw;
    }
}

// Error boundary — antes de tudo, para capturar qualquer exceção do pipeline e nunca
// vazar detalhes internos (stack trace, mensagem) ao cliente.
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();

// [Homologação em container] Aplica os cabeçalhos encaminhados ANTES de HSTS/HttpsRedirection e da
// autenticação: assim todo o pipeline enxerga o esquema (https) e o IP reais vindos do proxy da hospedagem.
app.UseForwardedHeaders();

// [Baixo] HSTS + redirect HTTPS apenas FORA de Development. Em dev o loop roda em http://localhost
// (cookie Secure é isento no localhost) e habilitá-los ali quebraria o SPA. Em produção ambos são
// obrigatórios — o cookie Secure do refresh depende de HTTPS fim a fim. Atrás do proxy, o
// ForwardedHeaders já marcou Request.IsHttps=true, então não há loop de redirecionamento.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// [Homologação em container] Serve os arquivos estáticos do Angular (wwwroot) no MESMO domínio da API.
// Roda antes da autenticação: os assets do shell (JS/CSS) são públicos e fazem short-circuit aqui, sem
// passar pela FallbackPolicy. Em desenvolvimento (SPA no ng serve) o wwwroot pode não existir — no-op.
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(SpaCors);
app.UseRateLimiter();   // [Alto 4] antes da autenticação: blinda o próprio fluxo de login/refresh
app.UseAuthentication();
app.UseAuthorization();
// Defesa em profundidade: barra (403) tokens sem tenant válido ou cujo tenant diverge do X-Tenant.
app.UseMiddleware<TenantConsistencyMiddleware>();

// [AEGIS-AUD-048] Endpoints de health check. ANÔNIMOS (a FallbackPolicy exige autenticação por padrão) —
// a sonda de um orquestrador não carrega credencial. Resposta MÍNIMA e sanitizada (só o status geral e o
// de cada check), sem vazar detalhe interno. Liveness e readiness são superfícies DISTINTAS via tags.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("live"),
    ResponseWriter = HealthResponseWriter.WriteMinimalAsync,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteMinimalAsync,
}).AllowAnonymous();

app.MapControllers();

// [Homologação em container] Fallback do roteamento do Angular: qualquer rota que NÃO case com /api/*,
// /health/* ou um arquivo estático existente entrega o index.html. É o que faz o refresh e o deep-link de
// rotas aninhadas (ex.: /history) devolverem o SPA em vez de 404. AllowAnonymous porque a FallbackPolicy
// exige autenticação por padrão e o shell do SPA é público (a autenticação ocorre dentro do app).
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();
