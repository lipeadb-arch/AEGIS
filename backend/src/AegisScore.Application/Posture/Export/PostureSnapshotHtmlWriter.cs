using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using AegisScore.Domain;

namespace AegisScore.Application.Posture.Export;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Relatório HTML AUTOCONTIDO de uma fotografia KNIGHT — escritor PURO (sem EF, rede
/// ou relógio). Um único arquivo: dados como ilha JSON, estilo e script inline, abas, gráficos, filtros e
/// detalhes funcionando OFFLINE, sem sessão e sem CDN.
///
/// Segurança do conteúdo (a evidência é DADO, nunca marcação):
///   • os dados vão num <c>&lt;script type="application/json"&gt;</c> com o encoder JavaScript padrão, que escapa
///     <c>&lt; &gt; &amp; ' "</c> — texto de evidência não consegue fechar a tag nem injetar marcação;
///   • o script só escreve com textContent/createElement; links só para HTTPS validado no servidor;
///   • CSP restritiva por HASH (<c>default-src 'none'</c>): nada externo carrega e nenhum script não previsto roda;
///   • o cabeçalho estático (visível sem JavaScript) é codificado com o encoder HTML (acentos preservados).
/// </summary>
public static class PostureSnapshotHtmlWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false,
    };

    public static byte[] Write(PostureSnapshot snapshot, bool integrityVerified)
    {
        if (snapshot.Type != PostureSnapshotType.Knight)
            throw new PostureExportNotSupportedException("O relatório HTML está disponível para fotografias do AEGIS KNIGHT.");

        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified);
        var data = JsonSerializer.Serialize(model, Json);

        var css = KnightReportHtmlAssets.Css;
        var js = KnightReportHtmlAssets.Js;
        var csp = "default-src 'none'; img-src data:; style-src '" + Sha256(css) + "'; script-src '" + Sha256(js)
            + "'; base-uri 'none'; form-action 'none'";
        // frame-ancestors é ignorado em <meta> (só vale como cabeçalho HTTP); o download já sai com cabeçalhos próprios.

        var h = model.Header;
        // Texto de dado: & < > " ' escapados, acentos preservados. A CSP é constante do código (sem dado externo).
        string E(string? s) => Html.Encode(s ?? "");
        var title = "AEGIS KNIGHT · Relatório de postura" + (string.IsNullOrWhiteSpace(h.ClientName) ? "" : " · " + h.ClientName);
        var k = model.Kpis;

        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!doctype html>\n<html lang=\"pt-BR\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(csp).Append("\">\n")
          .Append("<meta name=\"referrer\" content=\"no-referrer\">\n")
          .Append("<meta name=\"generator\" content=\"AEGIS KNIGHT\">\n")
          .Append("<title>").Append(E(title)).Append("</title>\n")
          .Append("<style>").Append(css).Append("</style>\n</head>\n<body>\n")
          .Append("<header class=\"top\"><div class=\"wrap\">")
          .Append("<div class=\"brand\">AEGIS KNIGHT · Análise da postura de segurança multicloud</div>")
          .Append("<h1>Relatório de postura de segurança</h1>")
          .Append("<p class=\"sub\">").Append(E(string.IsNullOrWhiteSpace(h.ClientName) ? "Cliente não registrado na fotografia" : h.ClientName))
          .Append(" · ").Append(E(h.SourceLabel)).Append(h.IsDemo ? " · DEMONSTRAÇÃO (dados sintéticos)" : "").Append("</p>")
          .Append("<div class=\"meta\"><span>Publicado em ").Append(E(Utc(h.CapturedAt))).Append("</span>")
          .Append("<span>Coleta: ").Append(E(h.DataRecency is { } r ? Utc(r) : "—")).Append("</span>")
          .Append("<span>Fotografia ").Append(E(h.SnapshotId.ToString("D"))).Append("</span>")
          .Append("<span>Integridade: ").Append(h.IntegrityVerified ? "hash verificado" : "não verificada").Append("</span></div>")
          .Append("</div></header>\n<main class=\"wrap\" id=\"app\">\n")
          .Append("<noscript><div class=\"panel\"><h2>Resumo</h2><p>Score KNIGHT: ")
          .Append(E(k.Score is { } sc ? Math.Round(sc).ToString(CultureInfo.InvariantCulture) : "sem avaliação"))
          .Append(" · Cobertura: ").Append(E(k.Coverage.ToString("0.#", CultureInfo.InvariantCulture))).Append("% · ")
          .Append(k.TotalControls).Append(" controle(s): ").Append(k.Passed).Append(" aprovado(s), ").Append(k.Failed)
          .Append(" reprovado(s), ").Append(k.NotEvaluated).Append(" não avaliado(s).</p>")
          .Append("<p>Os gráficos, filtros e detalhes exigem JavaScript habilitado no navegador. Nenhum recurso externo é carregado.</p></div></noscript>\n")
          .Append("</main>\n<footer class=\"wrap\">Gerado pelo AEGIS a partir da fotografia imutável ")
          .Append(E(h.SnapshotId.ToString("D"))).Append(" (SHA-256 ").Append(E(h.ContentHash)).Append("). Resultados determinísticos; a narrativa consultiva não altera nota, resultado ou severidade.</footer>\n")
          .Append("<script type=\"application/json\" id=\"aegis-data\">").Append(data).Append("</script>\n")
          .Append("<script>").Append(js).Append("</script>\n</body>\n</html>\n");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    private static string Sha256(string content) =>
        "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string Utc(DateTimeOffset v) =>
        v.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>Formato não disponível para o tipo de fotografia pedido (o controller responde 400).</summary>
public sealed class PostureExportNotSupportedException : Exception
{
    public PostureExportNotSupportedException(string message) : base(message) { }
}
