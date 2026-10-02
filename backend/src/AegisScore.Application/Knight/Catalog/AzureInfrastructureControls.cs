using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;
using static AegisScore.Application.Knight.Catalog.AzureRuleKit;
using static AegisScore.Application.Knight.Catalog.KnightRuleKit;

namespace AegisScore.Application.Knight.Catalog;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Controles do Azure: rede, armazenamento e Key Vault
// ============================================================================

/// <summary>Leituras comuns de rede.</summary>
internal static class AzureNet
{
    private static readonly HashSet<string> AnySource = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "Internet", "Any", "0.0.0.0", "0.0.0.0/0", "::/0", "<nw>/0", "/0",
    };

    /// <summary>Regras de entrada PERMITIDAS de qualquer origem que alcançam a porta/protocolo.</summary>
    public static List<string> OpenRules(AzureResource nsg, int port, string protocol)
    {
        var open = new List<string>();
        foreach (var r in nsg.Items("rules:items"))
        {
            if (!string.Equals(AzureJson.Str(r, "properties.direction"), "Inbound", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(AzureJson.Str(r, "properties.access"), "Allow", StringComparison.OrdinalIgnoreCase)) continue;
            var proto = AzureJson.Str(r, "properties.protocol") ?? "*";
            if (proto != "*" && !string.Equals(proto, protocol, StringComparison.OrdinalIgnoreCase)) continue;
            var sources = new List<string>();
            if (AzureJson.Str(r, "properties.sourceAddressPrefix") is { } sp) sources.Add(sp);
            sources.AddRange(AzureJson.Items(r, "properties.sourceAddressPrefixes").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            if (!sources.Any(AnySource.Contains)) continue;
            var ranges = new List<string>();
            if (AzureJson.Str(r, "properties.destinationPortRange") is { } dp) ranges.Add(dp);
            ranges.AddRange(AzureJson.Items(r, "properties.destinationPortRanges").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            if (ranges.Any(x => Covers(x, port))) open.Add(AzureJson.Str(r, "name") ?? "?");
        }
        return open;
    }

    internal static bool Covers(string range, int port)
    {
        range = range.Trim();
        if (range == "*") return true;
        var dash = range.IndexOf('-');
        if (dash > 0 && int.TryParse(range[..dash], out var a) && int.TryParse(range[(dash + 1)..], out var b)) return port >= a && port <= b;
        return int.TryParse(range, out var p) && p == port;
    }

    public static KnightItemCheck Port(AzureResource nsg, int port, string protocol, string service)
    {
        if (!nsg.Has("rules:items")) return KnightItemCheck.Of(null, "regras do NSG não informadas", $"nenhuma regra de entrada liberando {service} da internet");
        var open = OpenRules(nsg, port, protocol);
        return KnightItemCheck.Of(open.Count == 0,
            open.Count == 0 ? $"nenhuma regra de entrada libera {service} ({protocol.ToUpperInvariant()} {port}) de qualquer origem"
                : $"regra(s) {List(open, 4)} liberam {service} ({protocol.ToUpperInvariant()} {port}) de qualquer origem",
            $"nenhuma regra de entrada liberando {service} da internet");
    }

    /// <summary>Logs de fluxo (de todos os Network Watchers do escopo) cujo alvo é o recurso.</summary>
    public static (List<JsonElement> Logs, bool Unknown) FlowLogsFor(AzureResource target, AzureView v)
    {
        var logs = new List<JsonElement>();
        var unknown = false;
        foreach (var w in v.OfType("Microsoft.Network/networkWatchers"))
        {
            if (!w.Has("flowLogs:items"))
            {
                if (string.Equals(w.SubscriptionId, target.SubscriptionId, StringComparison.OrdinalIgnoreCase)) unknown = true;
                continue;
            }
            logs.AddRange(w.Items("flowLogs:items").Where(f =>
                string.Equals(AzureJson.Str(f, "properties.targetResourceId"), target.Id, StringComparison.OrdinalIgnoreCase)));
        }
        return (logs, unknown);
    }

    public static KnightItemCheck FlowLogToWorkspace(AzureResource target, AzureView v)
    {
        var (logs, unknown) = FlowLogsFor(target, v);
        var good = logs.Any(f => AzureJson.Bool(f, "properties.enabled") != false
            && AzureJson.Bool(f, "properties.flowAnalyticsConfiguration.networkWatcherFlowAnalyticsConfiguration.enabled") == true
            && AzureJson.Str(f, "properties.flowAnalyticsConfiguration.networkWatcherFlowAnalyticsConfiguration.workspaceResourceId") is not null);
        if (good) return KnightItemCheck.Of(true, "log de fluxo habilitado com análise de tráfego no Log Analytics", "log de fluxo com análise de tráfego (Log Analytics)");
        if (logs.Count == 0 && unknown)
            return KnightItemCheck.Of(null, "logs de fluxo do Network Watcher da assinatura não lidos", "log de fluxo com análise de tráfego (Log Analytics)");
        return KnightItemCheck.Of(false,
            logs.Count == 0 ? "nenhum log de fluxo tem este recurso como alvo" : "log de fluxo sem análise de tráfego habilitada (não chega ao Log Analytics)",
            "log de fluxo com análise de tráfego (Log Analytics)");
    }

    /// <summary>Logs de fluxo cujo alvo é do tipo pedido, como itens próprios.</summary>
    public static IEnumerable<AzureResource> FlowLogs(AzureView v, string targetType) =>
        v.OfType("Microsoft.Network/networkWatchers").SelectMany(w => w.Items("flowLogs:items")
            .Where(f => (AzureJson.Str(f, "properties.targetResourceId") ?? "").Contains("/providers/" + targetType + "/", StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var target = AzureJson.Str(f, "properties.targetResourceId") ?? "?";
                var name = target.Split('/').Last();
                return AzureView.Derived(w, target + "#flowlog", "Microsoft.Network/networkWatchers/flowLogs", $"log de fluxo de {name}", f);
            }));

    public static KnightItemCheck Retention(AzureResource flowLog)
    {
        var f = flowLog.Get("item")!.Value;
        var enabled = AzureJson.Bool(f, "properties.retentionPolicy.enabled");
        var days = AzureJson.Int(f, "properties.retentionPolicy.days");
        if (enabled == false)
            return KnightItemCheck.Of(null, "política de retenção do log de fluxo desligada — a exclusão fica por conta da conta de armazenamento, que não é lida aqui",
                "retenção de ao menos 90 dias (ou 0 = indefinida)");
        return KnightItemCheck.Of(days is null ? null : days == 0 || days >= 90,
            days is null ? "retenção não informada" : days == 0 ? "retenção indefinida (0)" : $"retenção de {days} dia(s)",
            "retenção de ao menos 90 dias (ou 0 = indefinida)");
    }

    /// <summary>Sub-redes das redes virtuais como itens próprios.</summary>
    public static IEnumerable<AzureResource> Subnets(AzureView v) =>
        v.OfType("Microsoft.Network/virtualNetworks").SelectMany(n => n.Items("subnets:items").Select(s =>
            AzureView.Derived(n, AzureJson.Str(s, "id") ?? $"{n.Id}/subnets/{AzureJson.Str(s, "name")}",
                "Microsoft.Network/virtualNetworks/subnets", $"{n.Name}/{AzureJson.Str(s, "name")}", s)));

    /// <summary>Sub-redes reservadas em que a plataforma não aceita (ou não exige) NSG.</summary>
    public static readonly HashSet<string> ReservedSubnets = new(StringComparer.OrdinalIgnoreCase)
    {
        "GatewaySubnet", "AzureFirewallSubnet", "AzureFirewallManagementSubnet", "RouteServerSubnet",
    };

    public static string? SubnetName(AzureResource subnet) => subnet.Get("item") is { } e ? AzureJson.Str(e, "name") : null;

    /// <summary>Regiões em uso (regiões com rede virtual) por assinatura.</summary>
    public static IEnumerable<AzureResource> RegionsInUse(AzureView v) =>
        v.OfType("Microsoft.Network/virtualNetworks").Where(n => n.Location is not null)
            .GroupBy(n => (Sub: n.SubscriptionId.ToLowerInvariant(), Loc: n.Location!.ToLowerInvariant()))
            .Select(g => AzureView.Derived(g.First(), $"/subscriptions/{g.Key.Sub}/locations/{g.Key.Loc}", "AEGIS/region", $"{g.Key.Loc}",
                location: g.Key.Loc) with { ResourceGroup = null });

    private static readonly Dictionary<string, string> PredefinedTls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AppGwSslPolicy20150501"] = "TLSv1_0",
        ["AppGwSslPolicy20170401"] = "TLSv1_1",
        ["AppGwSslPolicy20170401S"] = "TLSv1_2",
        ["AppGwSslPolicy20220101"] = "TLSv1_2",
        ["AppGwSslPolicy20220101S"] = "TLSv1_2",
    };

    public static KnightItemCheck GatewayTls(AzureResource gw)
    {
        var type = gw.Str("properties.sslPolicy.policyType");
        var name = gw.Str("properties.sslPolicy.policyName");
        var min = gw.Str("properties.sslPolicy.minProtocolVersion");
        string? effective = type?.ToLowerInvariant() switch
        {
            "predefined" => name is not null && PredefinedTls.TryGetValue(name, out var t) ? t : null,
            "custom" or "customv2" => min,
            _ => null,
        };
        if (!gw.Has("properties.sslPolicy"))
            return KnightItemCheck.Of(null, "política de TLS não definida — o padrão depende da versão da API usada na criação do gateway",
                "TLS mínimo 1.2");
        return KnightItemCheck.Of(effective is null ? null : effective is "TLSv1_2" or "TLSv1_3",
            $"política {type ?? "?"} {name ?? ""} → TLS mínimo {effective ?? "não determinado"}".Replace("  ", " "), "TLS mínimo 1.2");
    }
}

/// <summary>Leituras comuns de armazenamento e Key Vault.</summary>
internal static class AzureData
{
    public static IEnumerable<AzureResource> Accounts(AzureView v) => v.OfType("Microsoft.Storage/storageAccounts");

    /// <summary>Contas com serviço de arquivos (o tipo da conta suporta e a leitura não devolveu "não encontrado").</summary>
    public static IEnumerable<AzureResource> WithFiles(AzureView v) => Accounts(v).Where(a =>
        a.Kind is not ("BlobStorage" or "BlockBlobStorage") && a.Str("file:status") != "NotFound");

    public static IEnumerable<AzureResource> WithBlobs(AzureView v) => Accounts(v).Where(a =>
        a.Kind is not "FileStorage" && a.Str("blob:status") != "NotFound");

    /// <summary>Lista ";"-separada de um fato (versões e cifras do SMB).</summary>
    public static List<string> Semi(AzureResource r, string path) =>
        (r.Str(path) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Recurso protegido por bloqueio do nível pedido (no próprio recurso, no grupo ou na assinatura).</summary>
    public static KnightItemCheck Locked(AzureResource r, AzureView v, params string[] levels)
    {
        var locks = v.OfType("Microsoft.Authorization/locks").Where(l =>
        {
            var i = l.Id.IndexOf("/providers/Microsoft.Authorization/locks/", StringComparison.OrdinalIgnoreCase);
            if (i <= 0) return false;
            var scope = l.Id[..i];
            return (r.Id.Equals(scope, StringComparison.OrdinalIgnoreCase) || r.Id.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase))
                   && levels.Contains(l.Str("properties.level") ?? "", StringComparer.OrdinalIgnoreCase);
        }).ToList();
        return KnightItemCheck.Of(locks.Count > 0,
            locks.Count > 0 ? $"bloqueio(s) {List(locks.Select(l => $"{l.Name} ({l.Str("properties.level")})"), 3)}" : "nenhum bloqueio que impeça a exclusão",
            $"bloqueio {string.Join(" ou ", levels)} no recurso, no grupo ou na assinatura");
    }

    /// <summary>Itens de um fato lista de cofre (chaves, segredos, certificados) como itens próprios; leitura falha → item desconhecido.</summary>
    public static IEnumerable<AzureResource> VaultItems(AzureView v, string key, string typeSuffix, Func<AzureResource, bool> vaultFilter,
        Func<JsonElement, bool>? itemFilter = null)
    {
        foreach (var vault in v.OfType("Microsoft.KeyVault/vaults").Where(vaultFilter))
        {
            if (!vault.Has(key + ":items"))
            {
                yield return AzureView.Derived(vault, $"{vault.Id}/{typeSuffix}#nao-lidos", $"Microsoft.KeyVault/vaults/{typeSuffix}",
                    $"{vault.Name} ({typeSuffix} não lidos)");
                continue;
            }
            foreach (var e in vault.Items(key + ":items").Where(e => itemFilter?.Invoke(e) ?? true))
            {
                var name = AzureJson.Str(e, "name") ?? "?";
                yield return AzureView.Derived(vault, $"{vault.Id}/{typeSuffix}/{name}", $"Microsoft.KeyVault/vaults/{typeSuffix}", $"{vault.Name}/{name}", e);
            }
        }
    }

    public static bool Rbac(AzureResource vault) => vault.Bool("properties.enableRbacAuthorization") == true;

    public static KnightItemCheck Expiration(AzureResource item, AzureView v)
    {
        if (item.Get("item") is not { } e) return KnightItemCheck.Of(null, "itens do cofre não lidos nesta coleta", "data de expiração definida");
        var exp = Date(AzureJson.Prop(e, "properties.attributes.exp"));
        return KnightItemCheck.Of(exp is not null,
            exp is null ? "sem data de expiração" : exp < v.Now ? $"expirou em {Day(exp.Value)}" : $"expira em {Day(exp.Value)}",
            "data de expiração definida");
    }

    public static bool Enabled(JsonElement e) => AzureJson.Bool(e, "properties.attributes.enabled") != false;
}

public static class AzureInfrastructureControls
{
    private const KnightIndicatorCategory Cloud = KnightIndicatorCategory.CloudInfrastructure;

    private static IEnumerable<AzureResource> Nsgs(AzureView v) => v.OfType("Microsoft.Network/networkSecurityGroups");

    private static KnightIndicatorDefinition PortControl(string id, string service, int port, string protocol, string refKey, SeverityLevel sev) =>
        Control(KnightService.AzureNetworking, id, $"{service} liberado da internet em grupos de segurança de rede", Cloud, sev,
            $"Remover ou restringir as regras de entrada que permitem {service} ({protocol.ToUpperInvariant()} {port}) de qualquer origem; usar o Azure Bastion, VPN ou origens nominais.",
            $"Nenhuma regra de entrada permitida de origem *, Internet, Any ou 0.0.0.0/0 que alcance {protocol.ToUpperInvariant()} {port}.",
            new AzureCheck(KnightCapability.AzureNetworking, "grupo de segurança de rede", Nsgs,
                (r, _) => AzureNet.Port(r, port, protocol, service),
                "{0} de {1} NSG(s) liberam " + service + " de qualquer origem da internet.",
                "Nenhum dos {0} NSG(s) libera " + service + " de qualquer origem da internet."),
            Ref(refKey));

    private static AzureCheck Storage(string noun, Func<AzureView, IEnumerable<AzureResource>> pop, Func<AzureResource, AzureView, KnightItemCheck> check,
        string exposed, string passed, params KnightCapability[] also) =>
        new(KnightCapability.AzureStorage, noun, pop, check, exposed, passed, also);

    private static AzureCheck Vaults(Func<AzureResource, AzureView, KnightItemCheck> check, string exposed, string passed) =>
        new(KnightCapability.AzureKeyVault, "cofre de chaves", v => v.OfType("Microsoft.KeyVault/vaults"), check, exposed, passed);

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        // ======== Rede =======================================================================================
        PortControl("AK-AZ-NET-001", "RDP", 3389, "Tcp", AzureRefs.Az + "7.1#rdp", SeverityLevel.High),
        PortControl("AK-AZ-NET-002", "SSH", 22, "Tcp", AzureRefs.Az + "7.2#ssh", SeverityLevel.High),
        PortControl("AK-AZ-NET-003", "DNS", 53, "Udp", AzureRefs.Az + "7.3#dns", SeverityLevel.Medium),
        PortControl("AK-AZ-NET-004", "NTP", 123, "Udp", AzureRefs.Az + "7.3#ntp", SeverityLevel.Medium),
        PortControl("AK-AZ-NET-005", "SNMP", 161, "Udp", AzureRefs.Az + "7.3#snmp", SeverityLevel.Medium),
        PortControl("AK-AZ-NET-006", "CLDAP", 389, "Udp", AzureRefs.Az + "7.3#cldap", SeverityLevel.Medium),
        PortControl("AK-AZ-NET-007", "SSDP", 1900, "Udp", AzureRefs.Az + "7.3#ssdp", SeverityLevel.Medium),
        PortControl("AK-AZ-NET-008", "HTTP", 80, "Tcp", AzureRefs.Az + "7.4#http", SeverityLevel.Medium),
        PortControl("AK-AZ-NET-009", "HTTPS", 443, "Tcp", AzureRefs.Az + "7.4#https", SeverityLevel.Low),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-010", "Logs de fluxo de NSG retidos por menos de 90 dias", Cloud, SeverityLevel.Medium,
            "Definir a retenção dos logs de fluxo de NSG em 90 dias ou mais (ou 0, retenção indefinida).",
            "Cada log de fluxo de NSG com política de retenção de ao menos 90 dias.",
            new AzureCheck(KnightCapability.AzureNetworking, "log de fluxo de NSG", v => AzureNet.FlowLogs(v, "Microsoft.Network/networkSecurityGroups"),
                (r, _) => AzureNet.Retention(r),
                "{0} de {1} log(s) de fluxo de NSG retêm os registros por menos de 90 dias.",
                "Os {0} log(s) de fluxo de NSG retêm os registros por ao menos 90 dias."),
            Ref(AzureRefs.Az + "7.5")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-011", "Regiões em uso sem Network Watcher", Cloud, SeverityLevel.Medium,
            "Manter um Network Watcher em cada região onde a assinatura tem rede virtual.",
            "Para cada assinatura e região com rede virtual, um Network Watcher na mesma região.",
            new AzureCheck(KnightCapability.AzureNetworking, "região com rede virtual", AzureNet.RegionsInUse,
                (r, v) =>
                {
                    var has = v.InSubscription("Microsoft.Network/networkWatchers", r.SubscriptionId)
                        .Any(w => string.Equals(w.Location, r.Location, StringComparison.OrdinalIgnoreCase));
                    return KnightItemCheck.Of(has, has ? "Network Watcher presente na região" : "nenhum Network Watcher na região", "Network Watcher na região");
                },
                "{0} de {1} região(ões) com rede virtual não têm Network Watcher.",
                "As {0} região(ões) com rede virtual têm Network Watcher."),
            Ref(AzureRefs.Az + "7.6")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-012", "Logs de fluxo de rede virtual retidos por menos de 90 dias", Cloud, SeverityLevel.Low,
            "Definir a retenção dos logs de fluxo de rede virtual em 90 dias ou mais (ou 0, retenção indefinida).",
            "Cada log de fluxo de rede virtual com política de retenção de ao menos 90 dias.",
            new AzureCheck(KnightCapability.AzureNetworking, "log de fluxo de rede virtual", v => AzureNet.FlowLogs(v, "Microsoft.Network/virtualNetworks"),
                (r, _) => AzureNet.Retention(r),
                "{0} de {1} log(s) de fluxo de rede virtual retêm os registros por menos de 90 dias.",
                "Os {0} log(s) de fluxo de rede virtual retêm os registros por ao menos 90 dias."),
            Ref(AzureRefs.Az + "7.8")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-013", "VPN ponto a site aceita autenticação além do Entra ID", Cloud, SeverityLevel.Low,
            "Configurar a VPN ponto a site dos gateways para autenticar somente pelo Microsoft Entra ID (sem certificado nem RADIUS).",
            "Gateways com VPN ponto a site configurada: tipo de autenticação exclusivamente AAD.",
            new AzureCheck(KnightCapability.AzureNetworking, "gateway com VPN ponto a site",
                v => v.OfType("Microsoft.Network/virtualNetworkGateways")
                    .Where(g => g.Has("properties.vpnClientConfiguration.vpnClientAddressPool") || !g.Has("properties.gatewayType")),
                (g, _) =>
                {
                    if (!g.Has("properties.gatewayType")) return KnightItemCheck.Of(null, "configuração do gateway não lida", "somente AAD");
                    var types = g.Strings("properties.vpnClientConfiguration.vpnAuthenticationTypes");
                    return KnightItemCheck.Of(types.Count == 0 ? null : types.All(t => string.Equals(t, "AAD", StringComparison.OrdinalIgnoreCase)),
                        $"tipos de autenticação: {List(types)}", "somente AAD (Microsoft Entra ID)");
                },
                "{0} de {1} gateway(s) com VPN ponto a site aceitam autenticação fora do Entra ID.",
                "Os {0} gateway(s) com VPN ponto a site autenticam somente pelo Entra ID."),
            Ref(AzureRefs.Az + "7.9")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-014", "Gateways de aplicativo sem firewall de aplicativo web (WAF)", Cloud, SeverityLevel.Medium,
            "Usar a camada WAF (WAF_v2) nos gateways de aplicativo, com política de WAF associada e habilitada.",
            "Gateway de aplicativo na camada WAF/WAF_v2 com política de WAF ou configuração de WAF habilitada.",
            new AzureCheck(KnightCapability.AzureNetworking, "gateway de aplicativo", v => v.OfType("Microsoft.Network/applicationGateways"),
                (g, _) =>
                {
                    var tier = g.Str("properties.sku.tier");
                    var policy = g.Has("properties.firewallPolicy.id");
                    var legacy = g.Bool("properties.webApplicationFirewallConfiguration.enabled") == true;
                    var waf = tier is not null && tier.StartsWith("WAF", StringComparison.OrdinalIgnoreCase);
                    return KnightItemCheck.Of(tier is null ? null : waf && (policy || legacy),
                        $"camada {tier ?? "não informada"}; {(policy ? "política de WAF associada" : legacy ? "WAF legado habilitado" : "sem WAF habilitado")}",
                        "camada WAF com WAF habilitado");
                },
                "{0} de {1} gateway(s) de aplicativo não têm WAF habilitado.",
                "Os {0} gateway(s) de aplicativo têm WAF habilitado."),
            Ref(AzureRefs.Az + "7.10")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-015", "Sub-redes sem grupo de segurança de rede", Cloud, SeverityLevel.Medium,
            "Associar um NSG a cada sub-rede (exceto as sub-redes reservadas em que a plataforma não o aceita).",
            "Cada sub-rede, exceto GatewaySubnet, AzureFirewallSubnet, AzureFirewallManagementSubnet e RouteServerSubnet, com NSG associado.",
            new AzureCheck(KnightCapability.AzureNetworking, "sub-rede",
                v => AzureNet.Subnets(v).Where(s => !AzureNet.ReservedSubnets.Contains(AzureNet.SubnetName(s) ?? "")),
                (s, _) =>
                {
                    var nsg = s.Get("item") is { } e ? AzureJson.Str(e, "properties.networkSecurityGroup.id") : null;
                    return KnightItemCheck.Of(nsg is not null, nsg is null ? "sem NSG associado" : $"NSG {nsg.Split('/').Last()}", "NSG associado");
                },
                "{0} de {1} sub-rede(s) não têm NSG associado.",
                "As {0} sub-rede(s) têm NSG associado."),
            Ref(AzureRefs.Az + "7.11")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-016", "Gateways de aplicativo aceitam TLS abaixo de 1.2", Cloud, SeverityLevel.High,
            "Aplicar aos gateways de aplicativo uma política de TLS predefinida com mínimo 1.2 (ex.: AppGwSslPolicy20220101) ou personalizada com TLSv1_2 ou superior.",
            "Política de TLS do gateway com versão mínima TLS 1.2.",
            new AzureCheck(KnightCapability.AzureNetworking, "gateway de aplicativo", v => v.OfType("Microsoft.Network/applicationGateways"),
                (g, _) => AzureNet.GatewayTls(g),
                "{0} de {1} gateway(s) de aplicativo aceitam TLS abaixo de 1.2.",
                "Os {0} gateway(s) de aplicativo exigem TLS 1.2 ou superior."),
            Ref(AzureRefs.Az + "7.12")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-017", "Gateways de aplicativo sem HTTP/2", Cloud, SeverityLevel.Low,
            "Habilitar o HTTP/2 nos gateways de aplicativo.",
            "Propriedade enableHttp2 verdadeira em cada gateway de aplicativo.",
            new AzureCheck(KnightCapability.AzureNetworking, "gateway de aplicativo", v => v.OfType("Microsoft.Network/applicationGateways"),
                (g, _) => Flag(g, "properties.enableHttp2", true, "HTTP/2", "habilitado", "desabilitado"),
                "{0} de {1} gateway(s) de aplicativo estão sem HTTP/2.",
                "Os {0} gateway(s) de aplicativo têm HTTP/2 habilitado."),
            Ref(AzureRefs.Az + "7.13")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-018", "WAF do gateway de aplicativo sem inspeção do corpo da requisição", Cloud, SeverityLevel.Medium,
            "Habilitar a inspeção do corpo da requisição (requestBodyCheck) nas políticas de WAF dos gateways de aplicativo.",
            "Política de WAF com requestBodyCheck habilitado.",
            new AzureCheck(KnightCapability.AzureNetworking, "política de WAF", v => v.OfType("Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies"),
                (p, _) => Flag(p, "properties.policySettings.requestBodyCheck", true, "inspeção do corpo", "habilitada", "desabilitada"),
                "{0} de {1} política(s) de WAF não inspecionam o corpo das requisições.",
                "As {0} política(s) de WAF inspecionam o corpo das requisições."),
            Ref(AzureRefs.Az + "7.14")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-019", "WAF do gateway de aplicativo sem proteção contra bots", Cloud, SeverityLevel.Medium,
            "Adicionar o conjunto de regras gerenciado Microsoft_BotManagerRuleSet às políticas de WAF dos gateways de aplicativo.",
            "Política de WAF com o conjunto de regras Microsoft_BotManagerRuleSet.",
            new AzureCheck(KnightCapability.AzureNetworking, "política de WAF", v => v.OfType("Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies"),
                (p, _) =>
                {
                    if (!p.Has("properties.managedRules.managedRuleSets")) return KnightItemCheck.Of(null, "conjuntos de regras não informados", "Microsoft_BotManagerRuleSet presente");
                    var sets = p.Items("properties.managedRules.managedRuleSets").Select(s => AzureJson.Str(s, "ruleSetType") ?? "?").ToList();
                    return KnightItemCheck.Of(sets.Any(s => string.Equals(s, "Microsoft_BotManagerRuleSet", StringComparison.OrdinalIgnoreCase)),
                        $"conjuntos: {List(sets)}", "Microsoft_BotManagerRuleSet presente");
                },
                "{0} de {1} política(s) de WAF não têm proteção contra bots.",
                "As {0} política(s) de WAF têm proteção contra bots."),
            Ref(AzureRefs.Az + "7.15")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-020", "Assinaturas sem Azure Bastion", Cloud, SeverityLevel.Medium,
            "Implantar o Azure Bastion para o acesso administrativo às máquinas virtuais, em vez de expor RDP e SSH.",
            "Ao menos um host do Azure Bastion em cada assinatura do escopo.",
            new AzureCheck(KnightCapability.AzureNetworking, "assinatura", v => v.SubscriptionItems,
                (s, v) =>
                {
                    var n = v.InSubscription("Microsoft.Network/bastionHosts", s.SubscriptionId).Count();
                    return KnightItemCheck.Of(n > 0, $"{n} host(s) do Azure Bastion", "ao menos um host do Azure Bastion");
                },
                "{0} de {1} assinatura(s) não têm Azure Bastion.",
                "As {0} assinatura(s) têm Azure Bastion."),
            Ref(AzureRefs.Az + "8.4.1", KnightReferenceMatch.Partial,
                "Avalia a existência do Bastion em cada assinatura do escopo; um Bastion centralizado numa rede emparelhada de outra assinatura não é considerado.")),

        Control(KnightService.AzureNetworking, "AK-AZ-NET-021", "Redes virtuais sem proteção DDoS de rede", Cloud, SeverityLevel.Medium,
            "Associar as redes virtuais com recursos expostos a um plano de Proteção DDoS de Rede.",
            "Rede virtual com enableDdosProtection verdadeiro e plano de proteção DDoS associado.",
            new AzureCheck(KnightCapability.AzureNetworking, "rede virtual", v => v.OfType("Microsoft.Network/virtualNetworks"),
                (n, _) =>
                {
                    var on = n.Bool("properties.enableDdosProtection");
                    var plan = n.Has("properties.ddosProtectionPlan.id");
                    return KnightItemCheck.Of(on is null && !plan ? null : on == true && plan,
                        $"proteção DDoS {(on == true ? "habilitada" : on == false ? "desabilitada" : "não informada")}{(plan ? " com plano" : " sem plano")}",
                        "proteção DDoS habilitada com plano");
                },
                "{0} de {1} rede(s) virtual(is) estão sem proteção DDoS de rede.",
                "As {0} rede(s) virtual(is) têm proteção DDoS de rede."),
            Ref(AzureRefs.Az + "8.5")),

        // ======== Armazenamento ==============================================================================
        Control(KnightService.AzureStorage, "AK-AZ-STO-001", "Compartilhamentos de arquivos sem exclusão reversível", Cloud, SeverityLevel.Medium,
            "Habilitar a exclusão reversível de compartilhamentos de arquivos nas contas com o serviço de arquivos.",
            "Serviço de arquivos com shareDeleteRetentionPolicy habilitada.",
            Storage("conta com serviço de arquivos", AzureData.WithFiles,
                (a, _) => Flag(a, "file:properties.shareDeleteRetentionPolicy.enabled", true, "exclusão reversível de compartilhamentos", "habilitada", "desabilitada"),
                "{0} de {1} conta(s) com arquivos não protegem os compartilhamentos contra exclusão.",
                "As {0} conta(s) com arquivos têm exclusão reversível de compartilhamentos."),
            Ref(AzureRefs.Az + "9.1.1")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-002", "Compartilhamentos de arquivos aceitam SMB anterior a 3.1.1", Cloud, SeverityLevel.Medium,
            "Restringir as versões do SMB do serviço de arquivos a SMB 3.1.1.",
            "Configuração SMB do serviço de arquivos com versões = SMB3.1.1.",
            Storage("conta com serviço de arquivos", AzureData.WithFiles,
                (a, _) =>
                {
                    if (a.Str("file:status") is { } st) return KnightItemCheck.Of(null, $"serviço de arquivos não lido ({st})", "somente SMB3.1.1");
                    var versions = AzureData.Semi(a, "file:properties.protocolSettings.smb.versions");
                    return versions.Count == 0
                        ? KnightItemCheck.Of(false, "versões do SMB não definidas — padrão documentado: SMB 2.1, 3.0 e 3.1.1 aceitas", "somente SMB3.1.1")
                        : KnightItemCheck.Of(versions.All(x => string.Equals(x, "SMB3.1.1", StringComparison.OrdinalIgnoreCase)), $"versões: {string.Join(", ", versions)}", "somente SMB3.1.1");
                },
                "{0} de {1} conta(s) com arquivos aceitam versões do SMB anteriores a 3.1.1.",
                "As {0} conta(s) com arquivos aceitam somente SMB 3.1.1."),
            Ref(AzureRefs.Az + "9.1.2")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-003", "Criptografia de canal SMB abaixo de AES-256-GCM", Cloud, SeverityLevel.Medium,
            "Restringir a criptografia de canal do SMB a AES-256-GCM.",
            "Configuração SMB do serviço de arquivos com channelEncryption = AES-256-GCM.",
            Storage("conta com serviço de arquivos", AzureData.WithFiles,
                (a, _) =>
                {
                    if (a.Str("file:status") is { } st) return KnightItemCheck.Of(null, $"serviço de arquivos não lido ({st})", "somente AES-256-GCM");
                    var ciphers = AzureData.Semi(a, "file:properties.protocolSettings.smb.channelEncryption");
                    return ciphers.Count == 0
                        ? KnightItemCheck.Of(false, "cifras de canal não definidas — padrão documentado: AES-128-CCM, AES-128-GCM e AES-256-GCM aceitas", "somente AES-256-GCM")
                        : KnightItemCheck.Of(ciphers.All(x => string.Equals(x, "AES-256-GCM", StringComparison.OrdinalIgnoreCase)), $"cifras: {string.Join(", ", ciphers)}", "somente AES-256-GCM");
                },
                "{0} de {1} conta(s) com arquivos aceitam cifras de canal SMB abaixo de AES-256-GCM.",
                "As {0} conta(s) com arquivos exigem AES-256-GCM no canal SMB."),
            Ref(AzureRefs.Az + "9.1.3")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-004", "Blobs sem exclusão reversível", Cloud, SeverityLevel.Medium,
            "Habilitar a exclusão reversível de blobs (7 dias ou mais) nas contas com o serviço de blobs.",
            "Serviço de blobs com deleteRetentionPolicy habilitada.",
            Storage("conta com serviço de blobs", AzureData.WithBlobs,
                (a, _) => Flag(a, "blob:properties.deleteRetentionPolicy.enabled", true, "exclusão reversível de blobs", "habilitada", "desabilitada"),
                "{0} de {1} conta(s) com blobs não protegem os blobs contra exclusão.",
                "As {0} conta(s) com blobs têm exclusão reversível de blobs."),
            Ref(AzureRefs.Az + "9.2.1")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-005", "Contêineres de blobs sem exclusão reversível", Cloud, SeverityLevel.Medium,
            "Habilitar a exclusão reversível de contêineres nas contas com o serviço de blobs.",
            "Serviço de blobs com containerDeleteRetentionPolicy habilitada.",
            Storage("conta com serviço de blobs", AzureData.WithBlobs,
                (a, _) => Flag(a, "blob:properties.containerDeleteRetentionPolicy.enabled", true, "exclusão reversível de contêineres", "habilitada", "desabilitada"),
                "{0} de {1} conta(s) com blobs não protegem os contêineres contra exclusão.",
                "As {0} conta(s) com blobs têm exclusão reversível de contêineres."),
            Ref(AzureRefs.Az + "9.2.2")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-006", "Blobs sem controle de versão", Cloud, SeverityLevel.Medium,
            "Habilitar o controle de versão de blobs nas contas com o serviço de blobs.",
            "Serviço de blobs com isVersioningEnabled verdadeiro.",
            Storage("conta com serviço de blobs", AzureData.WithBlobs,
                (a, _) => Flag(a, "blob:properties.isVersioningEnabled", true, "controle de versão", "habilitado", "desabilitado"),
                "{0} de {1} conta(s) com blobs estão sem controle de versão.",
                "As {0} conta(s) com blobs têm controle de versão."),
            Ref(AzureRefs.Az + "9.2.3")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-007", "Contas de armazenamento sem lembrete de rotação de chaves", Cloud, SeverityLevel.Medium,
            "Definir a política de expiração de chaves (keyExpirationPeriodInDays) das contas de armazenamento, para que o Azure lembre de rotacionar as chaves.",
            "Política de chaves da conta com período de expiração definido.",
            Storage("conta de armazenamento", AzureData.Accounts,
                (a, _) =>
                {
                    var d = a.Int("properties.keyPolicy.keyExpirationPeriodInDays");
                    return KnightItemCheck.Of(d is > 0, d is > 0 ? $"lembrete a cada {d} dia(s)" : "sem política de expiração de chaves", "política de expiração de chaves definida");
                },
                "{0} de {1} conta(s) de armazenamento não têm lembrete de rotação de chaves.",
                "As {0} conta(s) de armazenamento têm lembrete de rotação de chaves."),
            Ref(AzureRefs.Az + "9.3.1.1")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-008", "Chaves de acesso de armazenamento não regeneradas há mais de 90 dias", Cloud, SeverityLevel.Medium,
            "Regenerar as duas chaves de acesso das contas de armazenamento ao menos a cada 90 dias (ou desligar o acesso por chave compartilhada).",
            "As duas chaves de acesso criadas ou regeneradas nos últimos 90 dias.",
            Storage("conta de armazenamento", AzureData.Accounts,
                (a, v) =>
                {
                    var k1 = Date(a.Get("properties.keyCreationTime") is { } kt ? AzureJson.Prop(kt, "key1") : null);
                    var k2 = Date(a.Get("properties.keyCreationTime") is { } kt2 ? AzureJson.Prop(kt2, "key2") : null);
                    if (k1 is null || k2 is null) return KnightItemCheck.Of(null, "data de criação das chaves não informada (contas antigas não a registram)", "chaves com menos de 90 dias");
                    var oldest = k1 < k2 ? k1.Value : k2.Value;
                    var days = (int)(v.Now - oldest).TotalDays;
                    return KnightItemCheck.Of(days <= 90, $"chave mais antiga criada em {Day(oldest)} ({days} dia(s))", "chaves com menos de 90 dias");
                },
                "{0} de {1} conta(s) de armazenamento têm chave de acesso com mais de 90 dias.",
                "As {0} conta(s) de armazenamento têm chaves regeneradas nos últimos 90 dias."),
            Ref(AzureRefs.Az + "9.3.1.2")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-009", "Acesso por chave compartilhada permitido nas contas de armazenamento", Cloud, SeverityLevel.Medium,
            "Desligar o acesso por chave compartilhada (allowSharedKeyAccess = false) e autorizar as requisições pelo Microsoft Entra ID.",
            "Propriedade allowSharedKeyAccess = false em cada conta.",
            Storage("conta de armazenamento", AzureData.Accounts,
                (a, _) => Flag(a, "properties.allowSharedKeyAccess", false, "acesso por chave compartilhada", "permitido", "bloqueado",
                    documentedDefault: true, defaultSource: "nulo equivale a verdadeiro"),
                "{0} de {1} conta(s) de armazenamento aceitam requisições autorizadas por chave compartilhada.",
                "As {0} conta(s) de armazenamento exigem autorização pelo Entra ID."),
            Ref(AzureRefs.Az + "9.3.1.3")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-010", "Contas de armazenamento sem endpoint privado", Cloud, SeverityLevel.Medium,
            "Acessar as contas de armazenamento por endpoint privado (Private Link).",
            "Ao menos uma conexão de endpoint privado aprovada em cada conta.",
            Storage("conta de armazenamento", AzureData.Accounts, (a, _) => PrivateEndpoint(a),
                "{0} de {1} conta(s) de armazenamento não têm endpoint privado aprovado.",
                "As {0} conta(s) de armazenamento têm endpoint privado aprovado."),
            Ref(AzureRefs.Az + "9.3.2.1")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-011", "Acesso público de rede habilitado nas contas de armazenamento", Cloud, SeverityLevel.Medium,
            "Desabilitar o acesso público de rede das contas de armazenamento e usar endpoints privados.",
            "Propriedade publicNetworkAccess = Disabled em cada conta.",
            Storage("conta de armazenamento", AzureData.Accounts, (a, _) => PublicAccessDisabled(a),
                "{0} de {1} conta(s) de armazenamento aceitam acesso pela rede pública.",
                "As {0} conta(s) de armazenamento têm o acesso público de rede desabilitado."),
            Ref(AzureRefs.Az + "9.3.2.2")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-012", "Regra de rede padrão das contas de armazenamento permite tudo", Cloud, SeverityLevel.Medium,
            "Definir a ação padrão do firewall das contas de armazenamento como Deny e liberar só redes e IPs nominais.",
            "networkAcls.defaultAction = Deny em cada conta.",
            Storage("conta de armazenamento", AzureData.Accounts,
                (a, _) => OneOf(a, "properties.networkAcls.defaultAction", "ação padrão do firewall", "Deny", "Deny"),
                "{0} de {1} conta(s) de armazenamento aceitam conexões de qualquer rede por padrão.",
                "As {0} conta(s) de armazenamento negam conexões por padrão."),
            Ref(AzureRefs.Az + "9.3.2.3")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-013", "Portal não usa autorização do Entra ID por padrão no armazenamento", Cloud, SeverityLevel.Medium,
            "Ligar a opção de usar a autorização do Microsoft Entra ID por padrão no portal do Azure (defaultToOAuthAuthentication).",
            "Propriedade defaultToOAuthAuthentication = true em cada conta.",
            Storage("conta de armazenamento", AzureData.Accounts,
                (a, _) => Flag(a, "properties.defaultToOAuthAuthentication", true, "autorização padrão do Entra ID no portal", "ligada", "desligada",
                    documentedDefault: false, defaultSource: "interpretação padrão: falso"),
                "{0} de {1} conta(s) de armazenamento usam a chave da conta por padrão no portal.",
                "As {0} conta(s) de armazenamento usam a autorização do Entra ID por padrão no portal."),
            Ref(AzureRefs.Az + "9.3.3.1")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-014", "Transferência segura não exigida nas contas de armazenamento", Cloud, SeverityLevel.Medium,
            "Exigir transferência segura (somente HTTPS) em todas as contas de armazenamento.",
            "Propriedade supportsHttpsTrafficOnly = true em cada conta.",
            Storage("conta de armazenamento", AzureData.Accounts,
                (a, _) => Flag(a, "properties.supportsHttpsTrafficOnly", true, "transferência segura", "exigida", "não exigida"),
                "{0} de {1} conta(s) de armazenamento aceitam tráfego sem HTTPS.",
                "As {0} conta(s) de armazenamento exigem transferência segura."),
            Ref(AzureRefs.Az + "9.3.4")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-015", "Firewall do armazenamento sem exceção para serviços confiáveis da Microsoft", Cloud, SeverityLevel.Medium,
            "Permitir os serviços confiáveis do Azure (bypass AzureServices) no firewall das contas de armazenamento.",
            "networkAcls.bypass contendo AzureServices em cada conta.",
            Storage("conta de armazenamento", AzureData.Accounts,
                (a, _) =>
                {
                    var bypass = a.Str("properties.networkAcls.bypass");
                    return KnightItemCheck.Of(bypass is null ? null : bypass.Contains("AzureServices", StringComparison.OrdinalIgnoreCase),
                        $"exceções do firewall: {bypass ?? "não informadas"}", "AzureServices entre as exceções");
                },
                "{0} de {1} conta(s) de armazenamento não autorizam os serviços confiáveis da Microsoft no firewall.",
                "As {0} conta(s) de armazenamento autorizam os serviços confiáveis da Microsoft no firewall."),
            Ref(AzureRefs.Az + "9.3.5")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-016", "Contas de armazenamento aceitam TLS abaixo de 1.2", Cloud, SeverityLevel.Medium,
            "Definir a versão mínima de TLS das contas de armazenamento como TLS 1.2.",
            "minimumTlsVersion = TLS1_2 (ou superior) em cada conta.",
            Storage("conta de armazenamento", AzureData.Accounts,
                (a, _) =>
                {
                    var t = a.Str("properties.minimumTlsVersion");
                    return t is null
                        ? KnightItemCheck.Of(false, "versão mínima não definida — interpretação padrão documentada: TLS 1.0", "TLS1_2 ou superior")
                        : KnightItemCheck.Of(t is "TLS1_2" or "TLS1_3", $"versão mínima: {t}", "TLS1_2 ou superior");
                },
                "{0} de {1} conta(s) de armazenamento aceitam TLS abaixo de 1.2.",
                "As {0} conta(s) de armazenamento exigem TLS 1.2 ou superior."),
            Ref(AzureRefs.Az + "9.3.6")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-017", "Replicação de objetos entre locatários permitida no armazenamento", Cloud, SeverityLevel.Medium,
            "Desabilitar a replicação de objetos entre locatários (allowCrossTenantReplication = false).",
            "Propriedade allowCrossTenantReplication = false em cada conta.",
            Storage("conta de armazenamento", AzureData.Accounts,
                (a, _) => Flag(a, "properties.allowCrossTenantReplication", false, "replicação entre locatários", "permitida", "bloqueada"),
                "{0} de {1} conta(s) de armazenamento permitem replicar objetos para outro locatário.",
                "As {0} conta(s) de armazenamento bloqueiam a replicação entre locatários."),
            Ref(AzureRefs.Az + "9.3.7")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-018", "Acesso anônimo a blobs permitido", Cloud, SeverityLevel.High,
            "Desabilitar o acesso anônimo a blobs (allowBlobPublicAccess = false) em todas as contas.",
            "Propriedade allowBlobPublicAccess = false em cada conta.",
            Storage("conta de armazenamento", AzureData.Accounts,
                (a, _) => Flag(a, "properties.allowBlobPublicAccess", false, "acesso anônimo a blobs", "permitido", "bloqueado"),
                "{0} de {1} conta(s) de armazenamento permitem acesso anônimo a blobs.",
                "As {0} conta(s) de armazenamento bloqueiam o acesso anônimo a blobs."),
            Ref(AzureRefs.Az + "9.3.8")),

        Control(KnightService.AzureStorage, "AK-AZ-STO-019", "Contas de armazenamento sem bloqueio contra exclusão", Cloud, SeverityLevel.Low,
            "Aplicar um bloqueio CanNotDelete às contas de armazenamento (no recurso, no grupo ou na assinatura).",
            "Bloqueio CanNotDelete ou ReadOnly cobrindo a conta.",
            Storage("conta de armazenamento", AzureData.Accounts, (a, v) => AzureData.Locked(a, v, "CanNotDelete", "ReadOnly"),
                "{0} de {1} conta(s) de armazenamento podem ser excluídas sem remover antes um bloqueio.",
                "As {0} conta(s) de armazenamento estão protegidas por bloqueio contra exclusão.",
                KnightCapability.AzureAuthorization),
            Ref(AzureRefs.Az + "9.3.9")),

        // ======== Key Vault ==================================================================================
        Control(KnightService.AzureKeyVault, "AK-AZ-KV-001", "Chaves sem data de expiração em cofres com RBAC", Cloud, SeverityLevel.Medium,
            "Definir data de expiração em todas as chaves habilitadas dos cofres que usam o modelo de permissões RBAC.",
            "Toda chave habilitada de cofre com RBAC tem atributo de expiração.",
            new AzureCheck(KnightCapability.AzureKeyVault, "chave em cofre com RBAC",
                v => AzureData.VaultItems(v, "keys", "keys", AzureData.Rbac, AzureData.Enabled), AzureData.Expiration,
                "{0} de {1} chave(s) em cofres com RBAC não têm data de expiração.",
                "As {0} chave(s) em cofres com RBAC têm data de expiração."),
            Ref(AzureRefs.Az + "8.3.1")),

        Control(KnightService.AzureKeyVault, "AK-AZ-KV-002", "Chaves sem data de expiração em cofres com política de acesso", Cloud, SeverityLevel.Medium,
            "Definir data de expiração em todas as chaves habilitadas dos cofres que usam políticas de acesso (sem RBAC).",
            "Toda chave habilitada de cofre sem RBAC tem atributo de expiração.",
            new AzureCheck(KnightCapability.AzureKeyVault, "chave em cofre sem RBAC",
                v => AzureData.VaultItems(v, "keys", "keys", x => !AzureData.Rbac(x), AzureData.Enabled), AzureData.Expiration,
                "{0} de {1} chave(s) em cofres sem RBAC não têm data de expiração.",
                "As {0} chave(s) em cofres sem RBAC têm data de expiração."),
            Ref(AzureRefs.Az + "8.3.2")),

        Control(KnightService.AzureKeyVault, "AK-AZ-KV-003", "Segredos sem data de expiração em cofres com RBAC", Cloud, SeverityLevel.Medium,
            "Definir data de expiração em todos os segredos habilitados dos cofres que usam o modelo de permissões RBAC.",
            "Todo segredo habilitado de cofre com RBAC tem atributo de expiração.",
            new AzureCheck(KnightCapability.AzureKeyVault, "segredo em cofre com RBAC",
                v => AzureData.VaultItems(v, "secrets", "secrets", AzureData.Rbac, AzureData.Enabled), AzureData.Expiration,
                "{0} de {1} segredo(s) em cofres com RBAC não têm data de expiração.",
                "Os {0} segredo(s) em cofres com RBAC têm data de expiração."),
            Ref(AzureRefs.Az + "8.3.3")),

        Control(KnightService.AzureKeyVault, "AK-AZ-KV-004", "Segredos sem data de expiração em cofres com política de acesso", Cloud, SeverityLevel.Medium,
            "Definir data de expiração em todos os segredos habilitados dos cofres que usam políticas de acesso (sem RBAC).",
            "Todo segredo habilitado de cofre sem RBAC tem atributo de expiração.",
            new AzureCheck(KnightCapability.AzureKeyVault, "segredo em cofre sem RBAC",
                v => AzureData.VaultItems(v, "secrets", "secrets", x => !AzureData.Rbac(x), AzureData.Enabled), AzureData.Expiration,
                "{0} de {1} segredo(s) em cofres sem RBAC não têm data de expiração.",
                "Os {0} segredo(s) em cofres sem RBAC têm data de expiração."),
            Ref(AzureRefs.Az + "8.3.4")),

        Control(KnightService.AzureKeyVault, "AK-AZ-KV-005", "Cofres de chaves sem proteção contra expurgo", Cloud, SeverityLevel.Medium,
            "Habilitar a proteção contra expurgo em todos os cofres de chaves.",
            "Propriedade enablePurgeProtection = true em cada cofre.",
            Vaults((k, _) => Flag(k, "properties.enablePurgeProtection", true, "proteção contra expurgo", "habilitada", "desabilitada",
                    documentedDefault: false, defaultSource: "ausente = não habilitada; uma vez ligada não pode ser desligada"),
                "{0} de {1} cofre(s) de chaves permitem expurgar itens excluídos antes do fim da retenção.",
                "Os {0} cofre(s) de chaves têm proteção contra expurgo."),
            Ref(AzureRefs.Az + "8.3.5")),

        Control(KnightService.AzureKeyVault, "AK-AZ-KV-006", "Cofres de chaves com políticas de acesso em vez de RBAC", Cloud, SeverityLevel.Medium,
            "Migrar os cofres de chaves para o modelo de permissões RBAC do Azure.",
            "Propriedade enableRbacAuthorization = true em cada cofre.",
            Vaults((k, _) => Flag(k, "properties.enableRbacAuthorization", true, "modelo de permissões RBAC", "habilitado", "desabilitado (políticas de acesso)",
                    documentedDefault: false, defaultSource: "ausente = políticas de acesso"),
                "{0} de {1} cofre(s) de chaves usam políticas de acesso em vez de RBAC.",
                "Os {0} cofre(s) de chaves usam o modelo de permissões RBAC."),
            Ref(AzureRefs.Az + "8.3.6")),

        Control(KnightService.AzureKeyVault, "AK-AZ-KV-007", "Acesso público de rede habilitado nos cofres de chaves", Cloud, SeverityLevel.Medium,
            "Desabilitar o acesso público de rede dos cofres de chaves e usar endpoints privados.",
            "Propriedade publicNetworkAccess = Disabled em cada cofre.",
            Vaults((k, _) => PublicAccessDisabled(k),
                "{0} de {1} cofre(s) de chaves aceitam acesso pela rede pública.",
                "Os {0} cofre(s) de chaves têm o acesso público de rede desabilitado."),
            Ref(AzureRefs.Az + "8.3.7")),

        Control(KnightService.AzureKeyVault, "AK-AZ-KV-008", "Cofres de chaves sem endpoint privado", Cloud, SeverityLevel.Medium,
            "Acessar os cofres de chaves por endpoint privado (Private Link).",
            "Ao menos uma conexão de endpoint privado aprovada em cada cofre.",
            Vaults((k, _) => PrivateEndpoint(k),
                "{0} de {1} cofre(s) de chaves não têm endpoint privado aprovado.",
                "Os {0} cofre(s) de chaves têm endpoint privado aprovado."),
            Ref(AzureRefs.Az + "8.3.8")),

        Control(KnightService.AzureKeyVault, "AK-AZ-KV-009", "Chaves sem rotação automática", Cloud, SeverityLevel.Medium,
            "Configurar política de rotação automática (ação Rotate) nas chaves habilitadas dos cofres.",
            "Toda chave habilitada com política de rotação contendo a ação Rotate.",
            new AzureCheck(KnightCapability.AzureKeyVault, "chave de cofre",
                v => AzureData.VaultItems(v, "keys", "keys", _ => true, AzureData.Enabled),
                (k, _) =>
                {
                    if (k.Get("item") is not { } e) return KnightItemCheck.Of(null, "chaves do cofre não lidas nesta coleta", "política de rotação com a ação Rotate");
                    var actions = AzureJson.Items(e, "properties.rotationPolicy.lifetimeActions")
                        .Select(a => AzureJson.Str(a, "action.type") ?? "?").ToList();
                    var rotate = actions.Any(a => string.Equals(a, "Rotate", StringComparison.OrdinalIgnoreCase));
                    return KnightItemCheck.Of(rotate, rotate ? "política de rotação automática" : actions.Count == 0 ? "sem política de rotação" : $"ações: {List(actions)} (sem Rotate)",
                        "política de rotação com a ação Rotate");
                },
                "{0} de {1} chave(s) não têm rotação automática.",
                "As {0} chave(s) têm rotação automática."),
            Ref(AzureRefs.Az + "8.3.9")),

        Control(KnightService.AzureKeyVault, "AK-AZ-KV-010", "Certificados do Key Vault com validade acima de 12 meses", Cloud, SeverityLevel.Medium,
            "Emitir os certificados dos cofres com validade de no máximo 12 meses (política do certificado).",
            "Política de cada certificado com validity_months ≤ 12.",
            new AzureCheck(KnightCapability.AzureKeyVault, "certificado de cofre",
                v => AzureData.VaultItems(v, "certs", "certificates", _ => true),
                (c, _) =>
                {
                    if (c.Get("item") is not { } e) return KnightItemCheck.Of(null, "certificados do cofre não lidos (plano de dados)", "validade de até 12 meses");
                    var m = AzureJson.Int(e, "validityMonths");
                    return KnightItemCheck.Of(m is null ? null : m <= 12, m is null ? "validade não informada" : $"validade de {m} mês(es)", "validade de até 12 meses");
                },
                "{0} de {1} certificado(s) têm validade acima de 12 meses.",
                "Os {0} certificado(s) têm validade de até 12 meses.",
                new[] { KnightCapability.AzureKeyVaultCertificates }),
            Ref(AzureRefs.Az + "8.3.11")),
    };
}
