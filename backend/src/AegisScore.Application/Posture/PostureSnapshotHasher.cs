using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AegisScore.Domain;

namespace AegisScore.Application.Posture;

/// <summary>
/// [AEGIS-AUD-036] Hash DETERMINÍSTICO e INEQUÍVOCO do conteúdo publicado de uma fotografia de postura — a prova
/// de integridade que permite detectar alteração indevida. É uma função PURA (sem EF, rede ou relógio): a mesma
/// fotografia produz sempre o mesmo hash, e o hash é RE-derivável da linha persistida (todo campo hasheado é
/// armazenado). A representação canônica é INEQUÍVOCA por construção: cada campo carrega um marcador de tipo,
/// strings são LENGTH-PREFIXED (um valor que contenha delimitadores, newline ou Unicode NÃO cria colisão
/// estrutural) e listas são ordenadas e prefixadas pela contagem. Doubles usam round-trip ("R"); timestamps são
/// truncados a MICROSSEGUNDOS (a precisão do <c>timestamptz</c> do PostgreSQL) para re-derivar em qualquer
/// backend; <c>null</c> é distinto de vazio e de zero.
///
/// NÃO entram no hash: o <see cref="Entity.Id"/> (identidade da linha, não conteúdo), os timestamps de auditoria
/// (<see cref="Entity.CreatedAt"/>/<see cref="Entity.UpdatedAt"/>) nem o próprio <see cref="PostureSnapshot.ContentHash"/>.
/// </summary>
public static class PostureSnapshotHasher
{
    /// <summary>Prefixo versionado da representação canônica — muda junto com o algoritmo, isolando o histórico.</summary>
    private const string CanonicalVersion = "posture-hash-v2";

    /// <summary>Computa o hash SHA-256 (hex minúsculo, 64 chars) do conteúdo canônico da fotografia.</summary>
    public static string Compute(PostureSnapshot s)
    {
        var canonical = BuildCanonical(s);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Recalcula o hash da fotografia e compara com o <see cref="PostureSnapshot.ContentHash"/> gravado.</summary>
    public static bool Verify(PostureSnapshot s) =>
        string.Equals(Compute(s), s.ContentHash, StringComparison.Ordinal);

    private static string BuildCanonical(PostureSnapshot s)
    {
        var w = new CanonicalWriter();
        w.Str(CanonicalVersion)
         .Str(s.TenantId.ToString("D"))
         .Enum(s.Type)
         .Str(s.SchemaVersion)
         .Str(s.FormulaVersion)
         .Str(s.CatalogVersion)
         .Str(s.SemanticFamily)
         .EnumN(s.SourceType)         // KnightSourceType? — null é distinto de qualquer valor
         .Str(s.SourceLabel)          // string? — null é distinto de ""
         .Inst(s.CapturedAt)
         .Dbl(s.Score)                // double? — null é distinto de 0
         .Int(s.AchievedPoints)
         .Int(s.PossiblePoints)
         .Int(s.EligiblePoints)
         .Dbl(s.Coverage)
         .Int(s.EvaluatedItems)
         .Int(s.EligibleItems)
         .Int(s.CompliantCount)
         .Int(s.NonCompliantCount)
         .Int(s.MitigatedCount)
         .Int(s.NotEvaluatedCount)
         .Int(s.ErrorCount)
         .Int(s.NotApplicableCount)
         .Inst(s.DataRecency);        // DateTimeOffset? — null é distinto de qualquer instante

        // Controles NIST — ordenados por código (estável, independente da ordem de carga).
        var controls = s.Controls.OrderBy(c => c.SubcategoryCode, StringComparer.Ordinal).ToList();
        w.Int(controls.Count);
        foreach (var c in controls)
        {
            w.Str(c.SubcategoryCode)
             .Str(c.FunctionCode)
             .Bool(c.Evaluated)
             .EnumN(c.Status)         // ControlStatus? — null (não avaliado) é distinto de NonCompliant
             .EnumN(c.VerdictSource)  // VerdictSource? — null quando não avaliado
             .Int(c.AchievedPoints)
             .Int(c.MaxPoints)
             .Inst(c.EvaluatedAt);    // DateTimeOffset? — null quando não avaliado

            var refs = c.EvidenceRefs
                .OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.Source, StringComparer.Ordinal)
                .ThenBy(e => e.Reference, StringComparer.Ordinal)
                .ThenBy(e => e.CollectedAt).ToList();
            w.Int(refs.Count);
            foreach (var e in refs)
                w.Str(e.Kind).Str(e.Source).Str(e.Reference).Inst(e.CollectedAt);
        }

        // Indicadores KNIGHT — ordenados por IndicatorId. Inclui título e evidência sanitizada, além dos mapeamentos.
        var indicators = s.Indicators.OrderBy(i => i.IndicatorId, StringComparer.Ordinal).ToList();
        w.Int(indicators.Count);
        foreach (var i in indicators)
        {
            w.Str(i.IndicatorId)
             .Str(i.Title)
             .Str(i.Evidence)
             .Enum(i.Category)
             .Enum(i.Severity)
             .Enum(i.Status)
             .Int(i.AffectedObjectCount)
             .Enum(i.SourceType)
             .Inst(i.CollectedAt);

            var nist = i.NistCodes.OrderBy(x => x, StringComparer.Ordinal).ToList();
            w.Int(nist.Count);
            foreach (var code in nist) w.Str(code);

            var mitre = i.MitreTechniques.OrderBy(x => x, StringComparer.Ordinal).ToList();
            w.Int(mitre.Count);
            foreach (var t in mitre) w.Str(t);
        }

        return w.ToString();
    }

    /// <summary>
    /// Escritor de representação canônica INEQUÍVOCA: cada token tem um marcador de tipo; strings são
    /// length-prefixed (delimitadores/newline/Unicode no valor não criam colisão); números e instantes são
    /// autodelimitados. <c>null</c> tem um token próprio, distinto de vazio e de zero.
    /// </summary>
    private sealed class CanonicalWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly StringBuilder _sb = new(4096);

        /// <summary>String length-prefixed pela contagem de UTF-16 code units — fronteira do valor inequívoca.</summary>
        public CanonicalWriter Str(string? v)
        {
            if (v is null) { _sb.Append("N;"); return this; }
            _sb.Append('S').Append(v.Length.ToString(Inv)).Append(':').Append(v).Append(';');
            return this;
        }

        public CanonicalWriter Int(long v) { _sb.Append('I').Append(v.ToString(Inv)).Append(';'); return this; }

        /// <summary>Double round-trip ("R") — sem perda por arredondamento textual; <c>null</c> distinto de 0.</summary>
        public CanonicalWriter Dbl(double? v)
        {
            _sb.Append(v is null ? "Dn;" : "D" + v.Value.ToString("R", Inv) + ";");
            return this;
        }

        public CanonicalWriter Bool(bool v) { _sb.Append('B').Append(v ? '1' : '0').Append(';'); return this; }

        /// <summary>Instante UTC TRUNCADO a microssegundos (precisão do PostgreSQL), como contagem de ticks.</summary>
        public CanonicalWriter Inst(DateTimeOffset? v)
        {
            if (v is null) { _sb.Append("Tn;"); return this; }
            var utc = v.Value.ToUniversalTime();
            var microTicks = utc.Ticks - (utc.Ticks % 10);   // 10 ticks = 1 µs
            _sb.Append('T').Append(microTicks.ToString(Inv)).Append(';');
            return this;
        }

        /// <summary>Enum pelo NOME, via <see cref="Str"/> — reordenar o enum não muda o hash.</summary>
        public CanonicalWriter Enum<TEnum>(TEnum v) where TEnum : struct, Enum => Str(v.ToString());

        /// <summary>Enum ANULÁVEL pelo NOME — <c>null</c> vira o token nulo (distinto de qualquer valor).</summary>
        public CanonicalWriter EnumN<TEnum>(TEnum? v) where TEnum : struct, Enum => Str(v?.ToString());

        public override string ToString() => _sb.ToString();
    }
}
