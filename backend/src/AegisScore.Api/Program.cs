using Microsoft.EntityFrameworkCore;
using AegisScore.Api;
using AegisScore.Api.Workers;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Microsoft;
using AegisScore.Infrastructure;
using AegisScore.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = "Aegis Score API", Version = "v1" }));

// Per-request tenant resolution (X-Tenant header) feeds the DbContext query filters.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

// Persistence + AI engine + connector registry + scoring services.
builder.Services.AddAegisScoreInfrastructure(builder.Configuration);

// Stack adapters (add Google/AWS/SIEM/EDR connector packages here).
builder.Services.AddMicrosoftConnectors();

// Document Hub: worker que lê os documentos enfileirados e mapeia os controles NIST.
builder.Services.AddHostedService<DocumentAnalysisWorker>();

const string SpaCors = "aegis-spa";
builder.Services.AddCors(o => o.AddPolicy(SpaCors, p => p
    .WithOrigins("http://localhost:5173", "http://localhost:5273", "http://localhost:3000")
    .AllowAnyHeader()
    .AllowAnyMethod()));

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
app.UseAuthorization();
app.MapControllers();

app.Run();
