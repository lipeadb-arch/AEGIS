using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using AegisScore.Api;
using AegisScore.Api.Workers;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Microsoft;
using AegisScore.Infrastructure;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
        };
    });
builder.Services.AddAuthorization(options =>
{
    // Secure-by-default: todo endpoint exige usuário autenticado, exceto os marcados com
    // [AllowAnonymous] (AuthController e, apenas em DEBUG, DevController). Qualquer controller novo
    // já nasce protegido, sem depender de o autor lembrar de anotar [Authorize].
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Stack adapters (add Google/AWS/SIEM/EDR connector packages here).
builder.Services.AddMicrosoftConnectors();

// Document Hub: worker que lê os documentos enfileirados e mapeia os controles NIST.
builder.Services.AddHostedService<DocumentAnalysisWorker>();

const string SpaCors = "aegis-spa";
builder.Services.AddCors(o => o.AddPolicy(SpaCors, p => p
    .WithOrigins("http://localhost:5173", "http://localhost:5273", "http://localhost:3000")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));   // necessário para o SPA enviar/receber o cookie HttpOnly de refresh

var app = builder.Build();

// Apply EF migrations and seed the NIST CSF 2.0 catalog on startup.
// The schema is owned exclusively by migrations now (no more EnsureCreated).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AegisScoreDbContext>();
    await db.Database.MigrateAsync();

    var catalogPath = builder.Configuration["Seed:CatalogPath"]
        ?? Path.Combine(app.Environment.ContentRootPath, "Data", "nist_csf_2_0_catalog.json");
    await FrameworkSeeder.SeedAsync(db, catalogPath);
}

// Error boundary — antes de tudo, para capturar qualquer exceção do pipeline e nunca
// vazar detalhes internos (stack trace, mensagem) ao cliente.
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(SpaCors);
app.UseAuthentication();
app.UseAuthorization();
// Defesa em profundidade: barra (403) tokens sem tenant válido ou cujo tenant diverge do X-Tenant.
app.UseMiddleware<TenantConsistencyMiddleware>();
app.MapControllers();

app.Run();
