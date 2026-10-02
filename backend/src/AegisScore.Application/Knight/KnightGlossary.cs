using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AegisScore.Application.Knight;

/// <summary>Um termo técnico: a sigla como aparece nos textos, o significado e uma explicação curta em português.</summary>
public sealed record KnightGlossaryTerm(string Term, string Meaning, string Explanation);

/// <summary>
/// [AEGIS-KNIGHT-PRESENTATION-01] Glossário ÚNICO dos termos técnicos do KNIGHT. A tela (pelo endpoint do glossário) e as
/// exportações (HTML e PDF, pelo modelo do relatório) leem DESTA lista — uma definição não pode divergir entre os formatos.
/// São termos genéricos de tecnologia: nada aqui descreve um controle. O que um controle avalia vem sempre do título e da
/// descrição congelados na fotografia, e os códigos internos (AK-…) não são interpretados por prefixo.
/// </summary>
public static class KnightGlossary
{
    public static IReadOnlyList<KnightGlossaryTerm> Terms { get; } = new KnightGlossaryTerm[]
    {
        new("AAD", "Azure Active Directory", "Nome anterior do Microsoft Entra ID, o diretório de identidades da Microsoft na nuvem."),
        new("AD", "Active Directory", "Diretório de identidades instalado nos servidores da própria organização (diretório local)."),
        new("API", "Interface de programação de aplicações (Application Programming Interface)", "Ponto de acesso programático de um serviço. O AEGIS lê as configurações pelas APIs oficiais dos provedores."),
        new("ASE", "Ambiente do Serviço de Aplicativo (App Service Environment)", "Implantação isolada e dedicada do Azure App Service, dentro da rede virtual do cliente."),
        new("B2B", "Colaboração entre empresas (Business-to-Business)", "Acesso de usuários de outras organizações como convidados ao ambiente."),
        new("CIS", "Center for Internet Security", "Organização que publica os CIS Benchmarks, guias de configuração segura usados como referência pelos controles."),
        new("CLDAP", "LDAP sem conexão (Connectionless LDAP)", "Variante do protocolo de diretório LDAP sobre UDP, frequentemente abusada em ataques de amplificação."),
        new("CNAME", "Nome canônico (Canonical Name)", "Registro DNS que faz um nome apontar para outro (alias)."),
        new("CORS", "Compartilhamento de recursos entre origens (Cross-Origin Resource Sharing)", "Regra que define quais sites podem chamar uma API a partir do navegador."),
        new("CPF", "Cadastro de Pessoas Físicas", "Número de identificação de pessoas no Brasil; aparece como tipo de informação sensível em políticas de proteção de dados."),
        new("CSPM", "Gestão da postura de segurança em nuvem (Cloud Security Posture Management)", "Avaliação contínua das configurações de nuvem contra boas práticas; no Microsoft Defender para Nuvem, é o plano que faz essa avaliação."),
        new("DBFS", "Sistema de arquivos do Databricks (Databricks File System)", "Armazenamento de arquivos associado a um workspace do Azure Databricks."),
        new("DKIM", "DomainKeys Identified Mail", "Assinatura criptográfica das mensagens enviadas pelo domínio, que permite ao destinatário verificar a origem."),
        new("DLP", "Prevenção contra perda de dados (Data Loss Prevention)", "Políticas que detectam e restringem o compartilhamento de informações sensíveis."),
        new("DMARC", "Domain-based Message Authentication, Reporting and Conformance", "Política publicada no DNS que diz ao destinatário o que fazer com mensagens que falham nas verificações SPF e DKIM."),
        new("DNS", "Sistema de nomes de domínio (Domain Name System)", "Sistema que traduz nomes em endereços. Zonas DNS privadas resolvem os endpoints privados dentro da rede."),
        new("EASM", "Gestão da superfície de ataque externa (External Attack Surface Management)", "Descoberta dos ativos da organização expostos na internet (Microsoft Defender EASM)."),
        new("EDR", "Detecção e resposta em endpoints (Endpoint Detection and Response)", "Monitoramento dos dispositivos para detectar e conter ataques."),
        new("FTP", "Protocolo de transferência de arquivos (File Transfer Protocol)", "Transferência de arquivos sem criptografia: credenciais e conteúdo trafegam legíveis."),
        new("FTPS", "FTP seguro (FTP over TLS)", "Transferência de arquivos protegida por TLS."),
        new("HTTP", "Hypertext Transfer Protocol", "Protocolo da web. Sem criptografia, o conteúdo trafega legível."),
        new("HTTPS", "HTTP seguro (HTTP over TLS)", "HTTP protegido por TLS: o conteúdo trafega cifrado."),
        new("IAM", "Gestão de identidades e acessos (Identity and Access Management)", "Conjunto de controles que define quem pode acessar o quê, com qual permissão e por quanto tempo."),
        new("ILB", "Balanceador de carga interno (Internal Load Balancer)", "Balanceador sem endereço público, acessível só pela rede interna."),
        new("IP", "Protocolo de internet (Internet Protocol)", "Protocolo de endereçamento; o endereço IP identifica um equipamento na rede."),
        new("LAPS", "Local Administrator Password Solution", "Gera e guarda senhas únicas e rotativas para a conta de administrador local de cada dispositivo."),
        new("M365", "Microsoft 365", "Conjunto de serviços de produtividade e colaboração da Microsoft (Exchange Online, SharePoint, Teams e outros)."),
        new("MDE", "Microsoft Defender para Endpoint", "Solução de proteção, detecção e resposta da Microsoft para dispositivos."),
        new("MFA", "Autenticação multifator (Multi-Factor Authentication)", "Exige mais de um fator para entrar — por exemplo, senha e aplicativo autenticador ou chave de segurança."),
        new("MITRE", "MITRE ATT&CK", "Base pública de táticas e técnicas usadas em ataques, mantida pela organização MITRE."),
        new("NIST", "National Institute of Standards and Technology", "Instituto de padrões dos EUA; publica o NIST Cybersecurity Framework (CSF), referência do AEGIS Score."),
        new("NSG", "Grupo de segurança de rede (Network Security Group)", "Conjunto de regras que permite ou bloqueia tráfego de entrada e saída em sub-redes e interfaces de rede do Azure."),
        new("NTP", "Protocolo de tempo de rede (Network Time Protocol)", "Sincronização de relógios; sobre UDP, é frequentemente abusado em ataques de amplificação."),
        new("PIM", "Privileged Identity Management", "Recurso do Microsoft Entra ID que torna papéis privilegiados elegíveis, ativados sob demanda e por tempo limitado."),
        new("RADIUS", "Remote Authentication Dial-In User Service", "Protocolo de autenticação centralizada usado, por exemplo, por gateways de VPN."),
        new("RBAC", "Controle de acesso baseado em papéis (Role-Based Access Control)", "Permissões concedidas por papéis atribuídos em um escopo. No Azure, define o que cada identidade pode fazer em assinaturas e recursos."),
        new("RDP", "Protocolo de área de trabalho remota (Remote Desktop Protocol)", "Acesso remoto gráfico a máquinas Windows; exposto à internet, é alvo frequente de ataques."),
        new("SAS", "Assinatura de acesso compartilhado (Shared Access Signature)", "Token que concede acesso delegado e temporário a recursos de armazenamento do Azure."),
        new("SCM", "Site de ferramentas do App Service (Kudu)", "Site de implantação e diagnóstico associado a cada aplicativo do Azure App Service."),
        new("SIEM", "Gestão de informações e eventos de segurança (Security Information and Event Management)", "Plataforma que centraliza e correlaciona registros de segurança para detecção e investigação."),
        new("SMB", "Server Message Block", "Protocolo de compartilhamento de arquivos; versões recentes permitem criptografia em trânsito."),
        new("SMS", "Mensagem de texto (Short Message Service)", "Como segundo fator de autenticação, é considerado fraco: pode ser interceptado ou redirecionado."),
        new("SMTP", "Simple Mail Transfer Protocol", "Protocolo de envio de e-mail. \"SMTP AUTH\" é a autenticação do cliente nesse protocolo."),
        new("SNMP", "Simple Network Management Protocol", "Gerência de equipamentos de rede; sobre UDP, é frequentemente abusado em ataques de amplificação."),
        new("SO", "Sistema operacional", "Software básico de uma máquina (Windows, Linux…)."),
        new("SPF", "Sender Policy Framework", "Registro DNS que lista os servidores autorizados a enviar e-mail em nome do domínio."),
        new("SQL", "Structured Query Language", "Linguagem de consulta de bancos de dados relacionais; aqui também designa os serviços Azure SQL."),
        new("SSDP", "Simple Service Discovery Protocol", "Descoberta de dispositivos na rede local; sobre UDP, é frequentemente abusado em ataques de amplificação."),
        new("SSH", "Secure Shell", "Acesso remoto cifrado por linha de comando; exposto à internet, é alvo frequente de ataques."),
        new("SSL", "Secure Sockets Layer", "Antecessor do TLS, hoje obsoleto; o nome ainda aparece em opções de conexão cifrada."),
        new("TCP", "Transmission Control Protocol", "Protocolo de transporte com conexão, usado pela maioria dos serviços (web, acesso remoto)."),
        new("TDE", "Criptografia transparente de dados (Transparent Data Encryption)", "Cifra os arquivos do banco de dados em repouso sem exigir mudança na aplicação."),
        new("TLS", "Transport Layer Security", "Protocolo que cifra a comunicação em trânsito. As versões 1.0 e 1.1 são consideradas inseguras."),
        new("UDP", "User Datagram Protocol", "Protocolo de transporte sem conexão; serviços UDP expostos podem ser usados em ataques de amplificação."),
        new("URL", "Endereço web (Uniform Resource Locator)", "Endereço que identifica um recurso na web."),
        new("VHD", "Disco rígido virtual (Virtual Hard Disk)", "Arquivo que contém o disco de uma máquina virtual."),
        new("VM", "Máquina virtual (Virtual Machine)", "Computador executado sobre infraestrutura de nuvem ou de virtualização."),
        new("VPN", "Rede virtual privada (Virtual Private Network)", "Túnel cifrado que liga redes, ou um usuário à rede da organização."),
        new("WAF", "Firewall de aplicação web (Web Application Firewall)", "Filtra requisições maliciosas antes que cheguem à aplicação."),
        new("WDATP", "Windows Defender Advanced Threat Protection", "Nome anterior do Microsoft Defender para Endpoint."),
        new("ZAP", "Limpeza automática em hora zero (Zero-hour Auto Purge)", "Remove das caixas de correio mensagens já entregues que depois foram identificadas como maliciosas."),
    };

    /// <summary>
    /// Explicação dos códigos internos dos controles. Não interpreta prefixos: o que o controle avalia é dito pelo título e
    /// pela descrição congelados com ele.
    /// </summary>
    public const string IdentifierExplanation =
        "Códigos como AK-AZ-IAM-004 são identificadores internos do catálogo do AEGIS KNIGHT: cada um aponta para um único " +
        "controle. O que o controle avalia está no título e na descrição registrados com ele nesta avaliação; consulte o código " +
        "para abrir o controle.";

    private static readonly Dictionary<string, Regex> Patterns = Terms.ToDictionary(
        t => t.Term,
        // Palavra inteira, com o plural em "s" ("APIs", "NSGs"). Hífen conta como parte da palavra: "AK-AZ-IAM-004" não é "IAM".
        t => new Regex(@"(?<![\p{L}\p{N}_-])" + Regex.Escape(t.Term) + @"s?(?![\p{L}\p{N}_-])", RegexOptions.CultureInvariant),
        StringComparer.Ordinal);

    /// <summary>Os termos que aparecem em algum dos textos, na ordem do glossário (alfabética).</summary>
    public static IReadOnlyList<KnightGlossaryTerm> UsedIn(IEnumerable<string?> texts)
    {
        var all = string.Join("\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
        return all.Length == 0 ? Array.Empty<KnightGlossaryTerm>() : Terms.Where(t => Patterns[t.Term].IsMatch(all)).ToList();
    }
}
