using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Connectors.Microsoft.Knight.Protection;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Consulta DNS de registros TXT (SPF e DMARC)
// ============================================================================
// SPF e DMARC são registros DNS PÚBLICOS: não há API do Microsoft 365 que diga se eles existem, e a própria Microsoft
// orienta a conferência por consulta DNS. O AEGIS consulta o resolvedor configurado no sistema (o mesmo que a
// aplicação já usa), só leitura, por uma implementação mínima do protocolo (RFC 1035 + EDNS0, RFC 6891), sem
// dependência externa. Três desfechos que não se confundem:
//   • RESOLVIDO com registros — os textos TXT publicados;
//   • RESOLVIDO sem registros (NXDOMAIN ou sem TXT) — "não publicado";
//   • NÃO RESOLVIDO (tempo esgotado, resolvedor indisponível) — o controle não é avaliado, nunca reprovado.

/// <summary>Resultado de uma consulta TXT.</summary>
public sealed record DnsTxtResult(bool Resolved, IReadOnlyList<string> Records, string? Failure)
{
    public static DnsTxtResult Found(IReadOnlyList<string> records) => new(true, records, null);
    public static DnsTxtResult NotResolved(string failure) => new(false, Array.Empty<string>(), failure);
}

public interface IDnsTxtResolver
{
    Task<DnsTxtResult> ResolveTxtAsync(string name, CancellationToken ct);
}

public sealed class DnsTxtResolver : IDnsTxtResolver
{
    private const ushort TypeTxt = 16;
    private const ushort TypeOpt = 41;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    private readonly Func<IReadOnlyList<IPAddress>> _servers;

    public DnsTxtResolver() : this(SystemServers) { }

    internal DnsTxtResolver(Func<IReadOnlyList<IPAddress>> servers) => _servers = servers;

    public async Task<DnsTxtResult> ResolveTxtAsync(string name, CancellationToken ct)
    {
        if (!IsValidName(name)) return DnsTxtResult.NotResolved("nome de domínio inválido");
        var servers = _servers();
        if (servers.Count == 0) return DnsTxtResult.NotResolved("nenhum resolvedor DNS configurado no sistema");

        string? lastFailure = null;
        foreach (var server in servers.Take(3))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var id = (ushort)Random.Shared.Next(0, ushort.MaxValue);
                var query = BuildQuery(id, name);
                var response = await QueryUdpAsync(server, query, ct);
                if (response is not null && Truncated(response))
                    response = await QueryTcpAsync(server, query, ct);
                if (response is null) { lastFailure = "tempo esgotado"; continue; }
                var parsed = ParseResponse(response, id);
                if (parsed is not null) return parsed;
                lastFailure = "resposta DNS ilegível";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or TimeoutException)
            {
                lastFailure = "resolvedor DNS indisponível";
            }
        }
        return DnsTxtResult.NotResolved(lastFailure ?? "resolvedor DNS indisponível");
    }

    internal static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 253
        && name.Split('.').All(l => l.Length is > 0 and <= 63 && l.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'));

    internal static byte[] BuildQuery(ushort id, string name)
    {
        var buf = new List<byte>(64);
        void U16(ushort v) { buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        U16(id);
        U16(0x0100);          // consulta padrão, recursão desejada
        U16(1); U16(0); U16(0); U16(1);   // 1 pergunta, 1 registro adicional (OPT)
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            buf.Add((byte)label.Length);
            buf.AddRange(Encoding.ASCII.GetBytes(label));
        }
        buf.Add(0);
        U16(TypeTxt); U16(1); // TXT, IN
        // EDNS0: aceita respostas UDP de até 4096 bytes (SPF longos não são truncados).
        buf.Add(0); U16(TypeOpt); U16(4096); buf.AddRange(new byte[] { 0, 0, 0, 0 }); U16(0);
        return buf.ToArray();
    }

    private static bool Truncated(byte[] response) => response.Length >= 3 && (response[2] & 0x02) != 0;

    /// <summary>Interpreta a resposta: NXDOMAIN/sem TXT = resolvido e vazio; outro RCODE de erro = null (não resolvido).</summary>
    internal static DnsTxtResult? ParseResponse(byte[] r, ushort expectedId)
    {
        if (r.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(r) != expectedId) return null;
        var rcode = r[3] & 0x0F;
        if (rcode == 3) return DnsTxtResult.Found(Array.Empty<string>());   // NXDOMAIN: o nome não existe
        if (rcode != 0) return null;

        int qd = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(4)), an = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(6));
        var pos = 12;
        for (var i = 0; i < qd; i++) { if (!SkipName(r, ref pos)) return null; pos += 4; }

        var records = new List<string>();
        for (var i = 0; i < an; i++)
        {
            if (!SkipName(r, ref pos) || pos + 10 > r.Length) return null;
            var type = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(pos));
            var len = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(pos + 8));
            pos += 10;
            if (pos + len > r.Length) return null;
            if (type == TypeTxt)
            {
                // Um registro TXT é uma sequência de <tamanho><texto>; os pedaços compõem UM texto.
                var sb = new StringBuilder();
                var p = pos;
                while (p < pos + len)
                {
                    var l = r[p++];
                    if (p + l > pos + len) return null;
                    sb.Append(Encoding.ASCII.GetString(r, p, l));
                    p += l;
                }
                records.Add(sb.ToString());
            }
            pos += len;
        }
        return DnsTxtResult.Found(records);
    }

    private static bool SkipName(byte[] r, ref int pos)
    {
        while (pos < r.Length)
        {
            var l = r[pos];
            if (l == 0) { pos++; return true; }
            if ((l & 0xC0) == 0xC0) { pos += 2; return pos <= r.Length; }
            pos += l + 1;
        }
        return false;
    }

    private static async Task<byte[]?> QueryUdpAsync(IPAddress server, byte[] query, CancellationToken ct)
    {
        using var udp = new UdpClient(server.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        await udp.SendAsync(query, new IPEndPoint(server, 53), timeout.Token);
        try
        {
            var result = await udp.ReceiveAsync(timeout.Token);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<byte[]?> QueryTcpAsync(IPAddress server, byte[] query, CancellationToken ct)
    {
        using var tcp = new TcpClient(server.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        await tcp.ConnectAsync(server, 53, timeout.Token);
        var stream = tcp.GetStream();
        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, timeout.Token);
        var lenBuf = new byte[2];
        await stream.ReadExactlyAsync(lenBuf, timeout.Token);
        var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(lenBuf)];
        await stream.ReadExactlyAsync(response, timeout.Token);
        return response;
    }

    private static IReadOnlyList<IPAddress> SystemServers()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().DnsAddresses)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal))
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
    }
}
