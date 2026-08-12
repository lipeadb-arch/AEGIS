using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharp.Fonts;

namespace AegisScore.Infrastructure.Posture.Export;

/// <summary>
/// [AEGIS-AUD-034] Resolvedor de fontes do relatório PDF para a edição CORE do PDFsharp (sem GDI+/WPF/Office). Ele
/// NÃO enumera fontes pelo sistema gráfico: lê diretamente arquivos TrueType já INSTALADOS na máquina (leitura de
/// arquivo pura), procurando uma família sans-serif com cobertura Latina COMPLETA — o que garante a renderização
/// correta de acentos e caracteres portugueses sem depender do Office. É multiplataforma por melhor esforço:
/// procura primeiro nas fontes do Windows e, em seguida, nos diretórios usuais de Linux (DejaVu/Liberation).
///
/// Mapeia TODAS as solicitações do documento para uma única família descoberta (o relatório usa uma só). Quando o
/// arquivo específico de negrito/itálico não existe, resolve para o regular e sinaliza a SIMULAÇÃO ao PDFsharp.
/// </summary>
public sealed class PdfReportFontResolver : IFontResolver
{
    /// <summary>Nome de família usado pelo documento MigraDoc — encaminhado a este resolvedor.</summary>
    public const string FamilyName = "AegisReport";

    private const string KeyRegular = "aegis-report#regular";
    private const string KeyBold = "aegis-report#bold";
    private const string KeyItalic = "aegis-report#italic";
    private const string KeyBoldItalic = "aegis-report#bolditalic";

    private readonly IReadOnlyDictionary<string, byte[]> _fonts;
    private readonly bool _hasBold;
    private readonly bool _hasItalic;
    private readonly bool _hasBoldItalic;

    private PdfReportFontResolver(
        IReadOnlyDictionary<string, byte[]> fonts, bool hasBold, bool hasItalic, bool hasBoldItalic)
    {
        _fonts = fonts;
        _hasBold = hasBold;
        _hasItalic = hasItalic;
        _hasBoldItalic = hasBoldItalic;
    }

    /// <summary>
    /// Tenta montar o resolvedor a partir das fontes instaladas. Devolve <c>null</c> quando NENHUMA família
    /// candidata é encontrada (ambiente sem fontes usuais) — o chamador então recorre a outra estratégia.
    /// </summary>
    public static PdfReportFontResolver? TryCreate()
    {
        foreach (var family in Candidates())
        {
            var regular = Locate(family.Regular);
            if (regular is null) continue;

            var fonts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [KeyRegular] = File.ReadAllBytes(regular),
            };

            var bold = Locate(family.Bold);
            var italic = Locate(family.Italic);
            var boldItalic = Locate(family.BoldItalic);
            if (bold is not null) fonts[KeyBold] = File.ReadAllBytes(bold);
            if (italic is not null) fonts[KeyItalic] = File.ReadAllBytes(italic);
            if (boldItalic is not null) fonts[KeyBoldItalic] = File.ReadAllBytes(boldItalic);

            return new PdfReportFontResolver(fonts, bold is not null, italic is not null, boldItalic is not null);
        }
        return null;
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Encaminha para a face mais específica disponível; simula quando o arquivo dedicado não existe.
        if (isBold && isItalic)
        {
            if (_hasBoldItalic) return new FontResolverInfo(KeyBoldItalic, false, false);
            if (_hasBold) return new FontResolverInfo(KeyBold, false, true);
            if (_hasItalic) return new FontResolverInfo(KeyItalic, true, false);
            return new FontResolverInfo(KeyRegular, true, true);
        }
        if (isBold)
            return _hasBold ? new FontResolverInfo(KeyBold, false, false) : new FontResolverInfo(KeyRegular, true, false);
        if (isItalic)
            return _hasItalic ? new FontResolverInfo(KeyItalic, false, false) : new FontResolverInfo(KeyRegular, false, true);
        return new FontResolverInfo(KeyRegular, false, false);
    }

    public byte[] GetFont(string faceName) =>
        _fonts.TryGetValue(faceName, out var bytes) ? bytes : _fonts[KeyRegular];

    // ---- Descoberta dos arquivos de fonte ------------------------------------------------------------

    private sealed record FontFamilyFiles(string Regular, string Bold, string Italic, string BoldItalic);

    /// <summary>Famílias candidatas em ordem de preferência — nomes de arquivo típicos por plataforma.</summary>
    private static IEnumerable<FontFamilyFiles> Candidates()
    {
        // Windows (garantidas): Segoe UI, depois Arial, Verdana, Tahoma.
        yield return new("segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf");
        yield return new("arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf");
        yield return new("verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf");
        yield return new("tahoma.ttf", "tahomabd.ttf", "tahoma.ttf", "tahomabd.ttf");
        // Linux (best effort): DejaVu Sans, Liberation Sans.
        yield return new("DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVuSans-Oblique.ttf", "DejaVuSans-BoldOblique.ttf");
        yield return new("LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", "LiberationSans-Italic.ttf", "LiberationSans-BoldItalic.ttf");
    }

    private static readonly Lazy<IReadOnlyList<string>> SearchDirs = new(BuildSearchDirs);

    private static IReadOnlyList<string> BuildSearchDirs()
    {
        var dirs = new List<string>();
        void Add(string? d) { if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d)) dirs.Add(d!); }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));   // C:\Windows\Fonts no Windows
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("/usr/share/fonts");
        Add("/usr/local/share/fonts");
        if (!string.IsNullOrWhiteSpace(home))
        {
            Add(Path.Combine(home, ".fonts"));
            Add(Path.Combine(home, ".local", "share", "fonts"));
        }
        return dirs;
    }

    /// <summary>Procura um arquivo de fonte pelo nome: direto na pasta (Windows) ou recursivo (Linux). <c>null</c> se ausente.</summary>
    private static string? Locate(string fileName)
    {
        foreach (var dir in SearchDirs.Value)
        {
            var direct = Path.Combine(dir, fileName);
            if (File.Exists(direct)) return direct;

            try
            {
                var match = Directory
                    .EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories)
                    .FirstOrDefault(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
            catch (UnauthorizedAccessException) { /* diretório protegido — ignora e segue */ }
            catch (IOException) { /* travessia parcial — ignora e segue */ }
        }
        return null;
    }
}
