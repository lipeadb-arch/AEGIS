namespace AegisScore.Application.Posture.Export;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Estilo e script do relatório HTML do KNIGHT. Ficam INLINE no arquivo baixado —
/// sem CDN, sem fonte externa, sem requisição alguma — e são autorizados pela CSP por HASH, nunca por
/// 'unsafe-inline'. O script só escreve no DOM com textContent/createElement: nenhum dado da avaliação vira
/// HTML ou código, e só links HTTPS já validados no servidor viram âncoras.
/// </summary>
internal static class KnightReportHtmlAssets
{
    public const string Css = """
:root{--bg:#f5f7fb;--panel:#fff;--ink:#14213d;--ink2:#46536b;--muted:#6b778c;--line:#dde3ec;--accent:#0a7ea4;--accent-bg:#e6f4f9;
--ok:#1b7f3b;--ok-bg:#e7f5ec;--fail:#b3261e;--fail-bg:#fbe9e7;--warn:#9a5b00;--warn-bg:#fff4e0;--ne:#56627a;--ne-bg:#eef1f6;--err:#6b3fa0;--err-bg:#f1eafa;
--crit:#8e1b12;--high:#c2410c;--med:#a16207;--low:#1d5fb8;--info:#56627a;font-family:Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:var(--ink);}
@media (prefers-color-scheme:dark){:root{--bg:#0d1422;--panel:#141d2e;--ink:#e7ecf5;--ink2:#b8c2d6;--muted:#8e9ab2;--line:#26324a;--accent:#38c5ec;--accent-bg:#0f2a38;
--ok:#57c27a;--ok-bg:#12301f;--fail:#ff7a70;--fail-bg:#3a1714;--warn:#f0b24a;--warn-bg:#35270d;--ne:#a6b1c6;--ne-bg:#1d2638;--err:#c7a6f5;--err-bg:#2a1f3d;
--crit:#ff8a80;--high:#ff9f5a;--med:#f0c04a;--low:#7fb2ff;--info:#a6b1c6;}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);line-height:1.5;font-size:14px}
a{color:var(--accent)}a:focus-visible,button:focus-visible,input:focus-visible,select:focus-visible{outline:3px solid var(--accent);outline-offset:2px}
.wrap{max-width:1180px;margin:0 auto;padding:20px 16px 48px}
header.top{background:var(--panel);border-bottom:3px solid var(--accent);}
header.top .wrap{padding-top:18px;padding-bottom:14px}
.brand{font-size:12px;letter-spacing:.14em;text-transform:uppercase;color:var(--accent);font-weight:700}
h1{margin:4px 0 2px;font-size:24px}h2{font-size:17px;margin:0 0 10px}h3{font-size:14px;margin:14px 0 6px}
.sub{color:var(--ink2);margin:0}.meta{color:var(--muted);font-size:12px;margin-top:6px;display:flex;flex-wrap:wrap;gap:4px 14px}
.tabs{display:flex;gap:4px;margin:16px 0 0;border-bottom:1px solid var(--line)}
.tabs button{background:none;border:0;border-bottom:3px solid transparent;padding:10px 14px;font:inherit;font-weight:600;color:var(--ink2);cursor:pointer}
.tabs button[aria-selected=true]{color:var(--accent);border-bottom-color:var(--accent)}
.panel{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:16px;margin-top:14px}
.grid{display:grid;gap:14px}.g2{grid-template-columns:repeat(2,minmax(0,1fr))}.g4{grid-template-columns:repeat(4,minmax(0,1fr))}
@media (max-width:860px){.g2,.g4{grid-template-columns:minmax(0,1fr)}}
.kpi{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px}
.kpi .v{font-size:30px;font-weight:700;line-height:1.1}.kpi .l{font-weight:600}.kpi .n{color:var(--muted);font-size:12px;margin-top:4px}
.counts{display:flex;flex-wrap:wrap;gap:8px;margin-top:10px}
.cbtn{border:1px solid var(--line);background:var(--panel);border-radius:8px;padding:6px 10px;font:inherit;cursor:pointer;color:var(--ink)}
.cbtn b{font-size:16px;margin-right:4px}.cbtn:hover{border-color:var(--accent)}
.pill{display:inline-block;border-radius:999px;padding:1px 9px;font-size:12px;font-weight:600;white-space:nowrap}
.s-Passed{color:var(--ok);background:var(--ok-bg)}.s-Exposed{color:var(--fail);background:var(--fail-bg)}.s-Mitigated{color:var(--warn);background:var(--warn-bg)}
.s-NotEvaluated,.s-NotApplicable{color:var(--ne);background:var(--ne-bg)}.s-Error{color:var(--err);background:var(--err-bg)}
.v-Critical{color:var(--crit);border:1px solid var(--crit)}.v-High{color:var(--high);border:1px solid var(--high)}.v-Medium{color:var(--med);border:1px solid var(--med)}
.v-Low{color:var(--low);border:1px solid var(--low)}.v-Informational{color:var(--info);border:1px solid var(--info)}
.bars{display:flex;flex-direction:column;gap:6px}.bar{display:grid;grid-template-columns:150px minmax(0,1fr) 44px;gap:8px;align-items:center;
background:none;border:0;padding:3px 0;font:inherit;color:var(--ink);text-align:left;cursor:pointer;width:100%}
.bar:hover .track{outline:1px solid var(--accent)}.track{height:16px;background:var(--ne-bg);border-radius:4px;overflow:hidden;display:flex}
.seg{height:100%}.seg.Passed{background:var(--ok)}.seg.Exposed{background:var(--fail)}.seg.Mitigated{background:var(--warn)}.seg.NotEvaluated,.seg.NotApplicable{background:var(--ne)}.seg.Error{background:var(--err)}
.seg.sev-Critical{background:var(--crit)}.seg.sev-High{background:var(--high)}.seg.sev-Medium{background:var(--med)}.seg.sev-Low{background:var(--low)}.seg.sev-Informational{background:var(--info)}
.kpi details{color:var(--muted);font-size:12px;margin-top:6px}.kpi summary{cursor:pointer;width:fit-content}.bar .lbl{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.bar .num{text-align:right;font-weight:700}
.legend{display:flex;flex-wrap:wrap;gap:10px;font-size:12px;color:var(--ink2);margin-top:8px}.legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:4px;vertical-align:-1px}
table{border-collapse:collapse;width:100%;font-size:13px}th,td{text-align:left;padding:7px 8px;border-bottom:1px solid var(--line);vertical-align:top}
th{font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.04em}.tw{overflow-x:auto}
.mono{font-family:Consolas,Menlo,monospace;font-size:12px;color:var(--ink2);word-break:break-all}
.note{border-left:3px solid var(--warn);background:var(--warn-bg);padding:8px 12px;border-radius:0 8px 8px 0;margin:8px 0;color:var(--ink)}
.note.info{border-left-color:var(--accent);background:var(--accent-bg)}
.filters{display:grid;grid-template-columns:2fr repeat(6,minmax(0,1fr)) auto;gap:8px;align-items:end}
.cov{width:100%}.cov td,.cov th{text-align:right}.cov td:first-child,.cov th:first-child{text-align:left}
@media (max-width:980px){.filters{grid-template-columns:repeat(2,minmax(0,1fr))}}
.filters label{display:flex;flex-direction:column;font-size:12px;color:var(--muted);gap:3px}
.filters input,.filters select{font:inherit;padding:7px 8px;border:1px solid var(--line);border-radius:8px;background:var(--panel);color:var(--ink);min-width:0}
.btn{font:inherit;font-weight:600;border:1px solid var(--accent);color:var(--accent);background:var(--panel);border-radius:8px;padding:7px 12px;cursor:pointer}
.recorte{margin:10px 0 0;color:var(--ink2);font-size:13px}
.ctl{border:1px solid var(--line);border-radius:10px;margin-top:8px;background:var(--panel)}
.ctl>button{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:10px;width:100%;background:none;border:0;padding:12px 14px;font:inherit;color:var(--ink);text-align:left;cursor:pointer}
.ctl>button .t{font-weight:600}.ctl>button .s{color:var(--muted);font-size:12px;margin-top:3px}.ctl>button .tags{display:flex;gap:6px;align-items:flex-start;flex-wrap:wrap;justify-content:flex-end}
.ctl-body{padding:0 14px 14px;border-top:1px solid var(--line)}.sec{margin-top:12px}.sec h3{color:var(--accent);text-transform:uppercase;font-size:12px;letter-spacing:.06em}
.kv{display:grid;grid-template-columns:200px minmax(0,1fr);gap:4px 12px;font-size:13px}.kv .k{color:var(--muted)}
@media (max-width:700px){.kv{grid-template-columns:minmax(0,1fr)}.bar{grid-template-columns:100px minmax(0,1fr) 36px}}
.empty{color:var(--muted);font-style:italic}footer{color:var(--muted);font-size:12px;margin-top:24px;overflow-wrap:anywhere}
@media print{.tabs,.filters,.btn,.no-print{display:none!important}[hidden]{display:block!important}.panel,.ctl{break-inside:avoid}body{background:#fff}}
""";

    public const string Js = """
(function(){'use strict';
var D=JSON.parse(document.getElementById('aegis-data').textContent);
var ST={tab:'overview',f:{q:'',status:'',severity:'',platform:'',service:'',domain:'',framework:''}};
var STATUS=[['Exposed','Reprovado'],['Mitigated','Mitigado (atenção)'],['Error','Erro na avaliação'],['NotEvaluated','Não avaliado'],['Passed','Aprovado'],['NotApplicable','Não aplicável']];
var SEV=[['Critical','Crítico'],['High','Alto'],['Medium','Médio'],['Low','Baixo'],['Informational','Informativo']];
function el(t,a,c){var e=document.createElement(t);if(a){for(var k in a){var v=a[k];if(v===null||v===undefined||v===false)continue;
if(k==='text')e.textContent=String(v);else if(k==='cls')e.className=v;else if(k.indexOf('on')===0)e.addEventListener(k.slice(2),v);else e.setAttribute(k,String(v));}}
(c||[]).forEach(function(x){if(x===null||x===undefined)return;e.appendChild(typeof x==='string'?document.createTextNode(x):x);});return e;}
function link(url,text){if(typeof url!=='string'||url.indexOf('https://')!==0)return document.createTextNode(text);
return el('a',{href:url,target:'_blank',rel:'noopener noreferrer',text:text});}
function dt(iso){if(!iso)return'—';var d=new Date(iso);if(isNaN(d.getTime()))return'—';function p(n){return(n<10?'0':'')+n;}
return p(d.getUTCDate())+'/'+p(d.getUTCMonth()+1)+'/'+d.getUTCFullYear()+' '+p(d.getUTCHours())+':'+p(d.getUTCMinutes())+' UTC';}
function lbl(list,k){for(var i=0;i<list.length;i++)if(list[i][0]===k)return list[i][1];return k;}
function pill(cls,text){return el('span',{cls:'pill '+cls,text:text});}
function pct(v){return v===null||v===undefined?'—':(Math.round(v*10)/10).toString().replace('.',',')+'%';}
var main=document.getElementById('app');
function setTab(t){ST.tab=t;var b=document.querySelectorAll('.tabs button');for(var i=0;i<b.length;i++){var on=b[i].getAttribute('data-tab')===t;b[i].setAttribute('aria-selected',on?'true':'false');b[i].tabIndex=on?0:-1;}
document.getElementById('tab-overview').hidden=t!=='overview';document.getElementById('tab-controls').hidden=t!=='controls';}
function go(filters,open){ST.f={q:'',status:'',severity:'',platform:'',service:'',domain:'',framework:''};for(var k in filters)ST.f[k]=filters[k];syncFilterInputs();renderList(open);setTab('controls');
var h=document.getElementById('tab-controls');if(open){var c=document.getElementById('ctl-'+open);if(c){c.scrollIntoView();var b=c.querySelector('button');if(b)b.focus();}}else h.scrollIntoView();}
// ---------- Visão geral ----------
function help(t,body){return el('details',null,[el('summary',{text:t}),el('span',{text:body})]);}
function overview(){var k=D.kpis,box=el('div',{id:'tab-overview',role:'tabpanel','aria-labelledby':'t-overview'});
var g=el('div',{cls:'grid g4'});
g.appendChild(el('div',{cls:'kpi'},[el('div',{cls:'l',text:'Score de postura KNIGHT'}),el('div',{cls:'v',text:k.score===null?'—':String(Math.round(k.score))}),
el('div',{cls:'n',text:k.score===null?'Sem controle avaliado, não há nota.':'Escala própria do KNIGHT, de 0 a 100.'}),help('Como é calculado','Pondera a severidade e o resultado de cada controle avaliado (fórmula '+D.header.formulaVersion+'). Controles não avaliados ficam fora da nota: nunca contam como zero nem como aprovados.')]));
g.appendChild(el('div',{cls:'kpi'},[el('div',{cls:'l',text:'Aprovação'}),el('div',{cls:'v',text:pct(k.approvalPercent)}),el('div',{cls:'n',text:k.passed+' de '+k.evaluated+' controles avaliados foram aprovados.'}),help('O que significa','Proporção de aprovados entre os controles avaliados. Não é a nota: a nota também pesa a severidade.')]));
g.appendChild(el('div',{cls:'kpi'},[el('div',{cls:'l',text:'Cobertura do assessment'}),el('div',{cls:'v',text:pct(k.coverage)}),el('div',{cls:'n',text:'Parte dos controles que pôde ser verificada.'}),help('O que significa','Controles avaliados ÷ controles aplicáveis. Mostra quanto foi possível verificar, não se o ambiente está conforme.')]));
var ua=k.uniqueAffected===null?k.occurrences+' ocorrência(s); itens distintos não calculáveis nesta fotografia.':k.occurrences+' ocorrência(s) em '+(k.uniqueAffectedIsFloor?'pelo menos ':'')+(k.uniqueAffectedComposition||(k.uniqueAffected+' item(ns) distinto(s)'))+(k.uniqueAffectedIsFloor?' — a lista de algum controle está incompleta.':'.');
g.appendChild(el('div',{cls:'kpi'},[el('div',{cls:'l',text:'Controles com achados'}),el('div',{cls:'v',text:String(k.findings)}),el('div',{cls:'n',text:ua}),help('Como contar','Controles reprovados ou mitigados. Uma mesma conta, aplicação, papel ou política pode aparecer em mais de um controle: cada aparição é uma ocorrência; itens distintos contam uma vez.')]));
box.appendChild(g);
box.appendChild(el('p',{cls:'sub',text:'O score KNIGHT resume os controles de configuração desta avaliação — desta fonte e desta coleta. O AEGIS Score (NIST) é outra medida; as duas notas não se somam.'}));
var cp=el('div',{cls:'panel'},[el('h2',{text:'Controles por resultado'})]),cs=el('div',{cls:'counts'});
[['Passed',k.passed],['Exposed',k.failed],['Mitigated',k.mitigated],['NotEvaluated',k.notEvaluated],['Error',k.errors],['NotApplicable',k.notApplicable]].forEach(function(x){
cs.appendChild(el('button',{cls:'cbtn',type:'button',onclick:function(){go({status:x[0]});}},[el('b',{text:String(x[1])}),pill('s-'+x[0],lbl(STATUS,x[0]))]));});
cp.appendChild(cs);cp.appendChild(el('p',{cls:'sub',text:k.totalControls+' controle(s) no escopo desta avaliação. Não avaliado: a evidência faltou, foi insuficiente ou inconclusiva (inclui dado ou permissão ausente) — o motivo aparece em cada controle; reduz a cobertura e nunca aprova. Erro: a regra falhou ao avaliar. Mitigado: exposição com controle compensatório comprovado.'}));
box.appendChild(cp);
var two=el('div',{cls:'grid g2'});
var sp=el('div',{cls:'panel'},[el('h2',{text:'Controles com achados por severidade'}),el('p',{cls:'sub',text:'Controles reprovados ou mitigados. Clique para ver a lista.'})]),sb=el('div',{cls:'bars'});
var maxS=Math.max.apply(null,k.findingsBySeverity.map(function(x){return x.count;}).concat([1]));
k.findingsBySeverity.forEach(function(x){var tr=el('span',{cls:'track'}),s=el('span',{cls:'seg sev-'+x.key});s.style.width=(100*x.count/maxS)+'%';tr.appendChild(s);
sb.appendChild(el('button',{cls:'bar',type:'button','aria-label':x.label+': '+x.count+' finding(s)',onclick:function(){go({status:'findings',severity:x.key});}},[el('span',{cls:'lbl'},[pill('v-'+x.key,x.label)]),tr,el('span',{cls:'num',text:String(x.count)})]));});
sp.appendChild(sb);two.appendChild(sp);
two.appendChild(dist('Resultado por domínio de segurança',D.byDomain,'domain'));
box.appendChild(two);
if(D.byPlatform&&D.byPlatform.length)box.appendChild(dist('Resultado por plataforma',D.byPlatform,'platform'));
box.appendChild(dist('Resultado por serviço',D.byService,'service'));
if(D.referenceCoverage)box.appendChild(coverage());
var pr=el('div',{cls:'panel'},[el('h2',{text:'Riscos prioritários'}),el('p',{cls:'sub',text:'Até cinco controles reprovados, ordenados por severidade e, depois, pela quantidade afetada. Nenhum critério além do que a avaliação comprovou.'})]);
if(D.priorities.length===0)pr.appendChild(el('p',{cls:'empty',text:'Nenhum controle reprovado nesta avaliação.'}));
else{var ol=el('ol');D.priorities.forEach(function(p){ol.appendChild(el('li',null,[el('button',{cls:'cbtn',type:'button',onclick:function(){go({},p.controlId);}},[pill('v-'+p.severity,p.severityLabel),' ',el('span',{text:p.title})]),el('div',{cls:'meta',text:p.controlId+' · '+p.criterion})]));});pr.appendChild(ol);}
box.appendChild(pr);
var to=el('div',{cls:'panel'},[el('h2',{text:'Contas, aplicações e demais itens mais recorrentes'}),el('p',{cls:'sub',text:'Itens afetados (contas, aplicações, grupos, papéis, políticas…) que aparecem em mais controles reprovados ou mitigados.'})]);
if(D.topObjects.length===0)to.appendChild(el('p',{cls:'empty',text:D.kpis.uniqueAffected===null?'Esta fotografia não congelou os itens afetados.':'Nenhum item afetado preservado.'}));
else{var tb=el('tbody');D.topObjects.forEach(function(o){tb.appendChild(el('tr',null,[el('td',null,[el('div',{text:o.label}),el('div',{cls:'mono',text:o.externalId})]),el('td',{text:o.kindLabel}),el('td',{text:String(o.controlCount)}),el('td',{cls:'mono',text:o.controlIds.join(', ')})]));});
to.appendChild(el('div',{cls:'tw'},[el('table',null,[el('thead',null,[el('tr',null,[el('th',{text:'Item'}),el('th',{text:'Tipo'}),el('th',{text:'Controles'}),el('th',{text:'Quais'})])]),tb])]));}
box.appendChild(to);
box.appendChild(sources());
if(D.advisory)box.appendChild(advisory());
box.appendChild(integrity());
return box;}
function dist(title,rows,key){var p=el('div',{cls:'panel'},[el('h2',{text:title}),el('p',{cls:'sub',text:'Clique para ver os controles. Cores sempre acompanhadas do rótulo na legenda.'})]),b=el('div',{cls:'bars'});
var max=Math.max.apply(null,rows.map(function(r){return r.passed+r.failed+r.mitigated+r.notEvaluated+r.errors+r.notApplicable;}).concat([1]));
rows.forEach(function(r){var tot=r.passed+r.failed+r.mitigated+r.notEvaluated+r.errors+r.notApplicable,tr=el('span',{cls:'track'});
[['Passed',r.passed],['Exposed',r.failed],['Mitigated',r.mitigated],['Error',r.errors],['NotEvaluated',r.notEvaluated],['NotApplicable',r.notApplicable]].forEach(function(x){if(!x[1])return;var s=el('span',{cls:'seg '+x[0],title:lbl(STATUS,x[0])+': '+x[1]});s.style.width=(100*x[1]/max)+'%';tr.appendChild(s);});
var f={};f[key]=r.key;var desc=r.label+': '+r.failed+' reprovado(s), '+r.passed+' aprovado(s), '+(r.notEvaluated+r.errors)+' não avaliado(s)/erro, de '+tot;
b.appendChild(el('button',{cls:'bar',type:'button','aria-label':desc,title:desc,onclick:function(){go(f);}},[el('span',{cls:'lbl',text:r.label}),tr,el('span',{cls:'num',text:String(tot)})]));});
p.appendChild(b);var lg=el('div',{cls:'legend'});STATUS.forEach(function(s){var i=el('i',{cls:'seg '+s[0]});lg.appendChild(el('span',null,[i,s[1]]));});p.appendChild(lg);return p;}
function sources(){var h=D.header,p=el('div',{cls:'panel'},[el('h2',{text:'Fontes, datas e limitações de cobertura'})]);
p.appendChild(el('div',{cls:'kv'},[el('span',{cls:'k',text:'Fonte'}),el('span',{text:h.sourceLabel+' · provedor '+h.provider+(h.isDemo?' (demonstração sintética)':'')}),
el('span',{cls:'k',text:'Coleta mais recente'}),el('span',{text:dt(h.dataRecency)}),el('span',{cls:'k',text:'Fotografia publicada em'}),el('span',{text:dt(h.capturedAt)}),
el('span',{cls:'k',text:'Escopo desta avaliação'}),el('span',{text:'Controles avaliados a partir de '+h.sourceLabel+'. Serviços ainda não coletados não aparecem como avaliados; o que falta está na cobertura do catálogo de referência.'})]));
if(D.limitations.length===0&&D.legacyLimitations.length===0)p.appendChild(el('p',{cls:'sub',text:'Nenhuma limitação de coleta registrada: todas as capacidades desta fonte foram lidas.'}));
if(D.limitations.length){var tb=el('tbody');D.limitations.forEach(function(l){tb.appendChild(el('tr',null,[el('td',{text:l.capabilityLabel}),el('td',{text:l.causeLabel}),
el('td',null,[el('div',{text:l.detail||'—'}),l.requiredPermission?el('div',{cls:'mono',text:'Permissão exigida pela chamada implementada: '+l.requiredPermission}):null]),
el('td',{cls:'mono',text:l.affectedControls.length?l.affectedControls.join(', '):'nenhum controle ficou sem avaliação por isso'}),el('td',{text:l.guidance}),el('td',{text:dt(l.attemptedAt)})]));});
p.appendChild(el('h3',{text:'Limitações de coleta'}));p.appendChild(el('div',{cls:'tw'},[el('table',null,[el('thead',null,[el('tr',null,['Capacidade','Causa','Detalhe','Controles prejudicados','O que fazer','Tentativa'].map(function(x){return el('th',{text:x});}))]),tb])]));}
if(D.legacyLimitations.length){p.appendChild(el('h3',{text:'Limitações de coleta (registro da fotografia)'}));var ul=el('ul');D.legacyLimitations.forEach(function(x){ul.appendChild(el('li',{text:x}));});p.appendChild(ul);}
return p;}
function advisory(){var a=D.advisory,p=el('div',{cls:'panel'},[el('h2',{text:'Interpretação consultiva'}),el('p',{cls:'note info',text:(a.fromAi?'Gerada por IA':'Texto determinístico (IA indisponível ou desligada)')+' e congelada nesta fotografia. Não altera nota, resultado, severidade nem mapeamento.'}),el('p',{text:a.executiveSummary})]);
function lst(t,xs){if(!xs.length)return;p.appendChild(el('h3',{text:t}));var ol=el('ol');xs.forEach(function(x){ol.appendChild(el('li',{text:x}));});p.appendChild(ol);}
lst('Riscos destacados',a.priorityRisks);lst('Ações sugeridas',a.recommendedActions);lst('Lacunas de coleta',a.collectionGaps);return p;}
function integrity(){var h=D.header,p=el('div',{cls:'panel'},[el('h2',{text:'Integridade, versões e reconciliação'})]);
p.appendChild(el('div',{cls:'kv'},[el('span',{cls:'k',text:'Fotografia'}),el('span',{cls:'mono',text:h.snapshotId}),el('span',{cls:'k',text:'Avaliação de origem'}),el('span',{cls:'mono',text:h.runId||'não registrada nesta fotografia'}),
el('span',{cls:'k',text:'Hash do conteúdo (SHA-256)'}),el('span',{cls:'mono',text:h.contentHash+(h.integrityVerified?' · verificado na exportação':'')}),
el('span',{cls:'k',text:'Versões'}),el('span',{cls:'mono',text:'schema '+h.schemaVersion+' · catálogo '+h.catalogVersion+' · fórmula '+h.formulaVersion+(h.profileCatalogVersion?' · perfis '+h.profileCatalogVersion:'')})]));
p.appendChild(el('p',{cls:'sub',text:'Este arquivo contém a avaliação completa (todos os controles e itens congelados), não um recorte: os filtros da aba de controles afetam só a visualização. O CSV da mesma fotografia tem uma linha por item de cada controle (ou uma linha para o controle sem itens): controles = valores distintos de IndicatorId; ocorrências = linhas com ObjectRelation "Afetado" em controles reprovados ou mitigados; itens únicos = pares distintos (ObjectType, ObjectExternalId) dessas linhas.'}));
D.notes.forEach(function(n){p.appendChild(el('p',{cls:'note',text:n}));});return p;}
function coverage(){var c=D.referenceCoverage,p=el('div',{cls:'panel'},[el('h2',{text:'Cobertura do catálogo de referência'}),
el('p',{cls:'sub',text:'Três medidas diferentes, que não se somam: (1) cobertura do catálogo — o que o AEGIS consegue avaliar, propriedade do produto; (2) cobertura desta avaliação — o que a coleta conseguiu avaliar neste ambiente ('+pct(D.kpis.coverage)+'); (3) aprovação — o que foi avaliado e está conforme ('+pct(D.kpis.approvalPercent)+').'}),
el('p',{cls:'sub',text:c.frameworks.join(' · ')+' · catálogo '+c.catalogVersion+'. Integral = critério da referência; parcial = critério equivalente, não idêntico. Limitação da API oficial, verificação manual e acesso que o conector não tem nunca contam como avaliados.'})]);
var tb=el('tbody');[c.total].concat(c.byPlatform).forEach(function(r){tb.appendChild(el('tr',null,[el('td',{text:r.label}),el('td',{text:String(r.total)}),
el('td',{text:r.implemented+' ('+pct(r.fullPercent)+')'}),el('td',{text:r.partial+' ('+pct(r.partialPercent)+')'}),el('td',{text:String(r.pending)}),el('td',{text:String(r.apiLimitation)}),el('td',{text:String(r.manualOnly)}),el('td',{text:String(r.requiresAccess)})]));});
p.appendChild(el('div',{cls:'tw'},[el('table',{cls:'cov'},[el('thead',null,[el('tr',null,['Recorte','Total','Integral','Parcial','Pendente','Limitação da API','Manual','Outro acesso'].map(function(x){return el('th',{text:x});}))]),tb])]));
p.appendChild(el('p',{cls:'sub',text:'“Com alguma avaliação automatizada” (integral + parcial): '+pct(c.total.anyAutomatedPercent)+' — não é cobertura completa.'}));return p;}
// ---------- Controles e findings ----------
var listBox,recorte,inputs={};
function controlsTab(){var box=el('div',{id:'tab-controls',role:'tabpanel','aria-labelledby':'t-controls'});box.hidden=true;
var f=el('div',{cls:'panel filters',role:'search'});
function sel(name,label,opts){var s=el('select',{id:'f-'+name,onchange:function(){ST.f[name]=s.value;renderList();}},[el('option',{value:'',text:'Todos'})].concat(opts.map(function(o){return el('option',{value:o[0],text:o[1]});})));inputs[name]=s;return el('label',{'for':'f-'+name},[label,s]);}
var q=el('input',{id:'f-q',type:'search',placeholder:'Controle, código, conta, aplicação…',oninput:function(){ST.f.q=q.value;renderList();}});inputs.q=q;
f.appendChild(el('label',{'for':'f-q'},['Pesquisa',q]));
f.appendChild(sel('status','Resultado',[['findings','Findings (reprovado + mitigado)']].concat(STATUS)));
f.appendChild(sel('severity','Severidade',SEV));
f.appendChild(sel('platform','Plataforma',uniq(D.controls.map(function(c){return c.platform||c.provider;})).map(function(x){return[x,x];})));
f.appendChild(sel('service','Serviço',uniq(D.controls.map(function(c){return c.service;})).map(function(x){return[x,x];})));
f.appendChild(sel('domain','Domínio',uniqPairs(D.controls.map(function(c){return[c.domain,c.domainLabel];}))));
f.appendChild(sel('framework','Framework',D.frameworkOptions.map(function(x){return[x,x];})));
f.appendChild(el('button',{cls:'btn',type:'button',onclick:function(){go({});}, text:'Limpar filtros'}));
box.appendChild(f);recorte=el('p',{cls:'recorte',role:'status','aria-live':'polite'});box.appendChild(recorte);listBox=el('div');box.appendChild(listBox);return box;}
function uniq(a){var s={},o=[];a.forEach(function(x){if(x&&!s[x]){s[x]=1;o.push(x);}});return o.sort();}
function uniqPairs(a){var s={},o=[];a.forEach(function(x){if(!s[x[0]]){s[x[0]]=1;o.push(x);}});return o.sort(function(x,y){return x[1]<y[1]?-1:1;});}
function syncFilterInputs(){for(var k in inputs)inputs[k].value=ST.f[k]||'';}
function matches(c){var f=ST.f;
if(f.status==='findings'){if(c.status!=='Exposed'&&c.status!=='Mitigated')return false;}else if(f.status&&c.status!==f.status)return false;
if(f.severity&&c.severity!==f.severity)return false;if(f.platform&&(c.platform||c.provider)!==f.platform)return false;if(f.service&&c.service!==f.service)return false;if(f.domain&&c.domain!==f.domain)return false;
if(f.framework&&c.frameworks.indexOf(f.framework)<0)return false;
if(f.q){var t=f.q.toLowerCase(),hay=[c.id,c.title,c.description||'',c.evidence||'',c.service,c.domainLabel].join(' ').toLowerCase();
if(hay.indexOf(t)<0&&!c.objects.some(function(o){return[o.externalId,o.displayName||'',o.userPrincipalName||''].join(' ').toLowerCase().indexOf(t)>=0;}))return false;}
return true;}
function describeFilter(){var f=ST.f,p=[];if(f.status)p.push('resultado: '+(f.status==='findings'?'findings':lbl(STATUS,f.status)));if(f.severity)p.push('severidade: '+lbl(SEV,f.severity));
if(f.platform)p.push('plataforma: '+f.platform);if(f.service)p.push('serviço: '+f.service);if(f.domain){var d=D.controls.filter(function(c){return c.domain===f.domain;})[0];p.push('domínio: '+(d?d.domainLabel:f.domain));}
if(f.framework)p.push('framework: '+f.framework);if(f.q)p.push('pesquisa: “'+f.q+'”');return p.length?p.join(' · '):'sem filtros (avaliação completa)';}
function renderList(open){var list=D.controls.filter(matches);listBox.textContent='';
recorte.textContent='Mostrando '+list.length+' de '+D.controls.length+' controle(s) · Recorte: '+describeFilter()+' · os filtros afetam só a visualização; o arquivo contém a avaliação completa.';
if(!list.length){listBox.appendChild(el('p',{cls:'empty',text:'Nenhum controle corresponde aos filtros. Use “Limpar filtros”.'}));return;}
list.forEach(function(c){listBox.appendChild(controlItem(c,open===c.id));});}
function controlItem(c,isOpen){var body=detail(c);body.hidden=!isOpen;var id='ctl-'+c.id;
var head=el('button',{type:'button','aria-expanded':isOpen?'true':'false','aria-controls':id+'-b',onclick:function(){var o=head.getAttribute('aria-expanded')==='true';head.setAttribute('aria-expanded',o?'false':'true');body.hidden=o;}},
[el('span',null,[el('div',{cls:'t',text:c.title}),el('div',{cls:'s',text:c.id+' · '+c.service+' · '+c.domainLabel+(c.affectedCount>0?' · '+(c.affectedComposition||c.affectedCount+' afetado(s)'):'')})]),
el('span',{cls:'tags'},[pill('s-'+c.status,c.statusLabel),pill('v-'+c.severity,c.severityLabel)])]);
body.id=id+'-b';return el('div',{cls:'ctl',id:id},[head,body]);}
function sec(t,children){return el('div',{cls:'sec'},[el('h3',{text:t})].concat(children));}
function detail(c){var b=el('div',{cls:'ctl-body'});
b.appendChild(sec('O problema',[el('p',{text:c.description||'Descrição não congelada nesta fotografia.'}),el('div',{cls:'kv'},[el('span',{cls:'k',text:'O que foi encontrado'}),el('span',{text:c.evidence}),
c.notEvaluatedReason?el('span',{cls:'k',text:'Por que não foi avaliado'}):null,c.notEvaluatedReason?el('span',{text:c.notEvaluatedReason}):null])]));
b.appendChild(sec('Por que importa',[el('div',{cls:'kv'},[el('span',{cls:'k',text:'Risco'}),el('span',{text:c.rationale||'Risco não congelado nesta fotografia.'}),
c.impact?el('span',{cls:'k',text:'Impacto potencial'}):null,c.impact?el('span',{text:c.impact}):null]),c.doesNotProve?el('p',{cls:'note',text:'O que este resultado não comprova: '+c.doesNotProve}):null]));
b.appendChild(sec('Onde foi encontrado',[c.provenReach?el('p',{text:c.provenReach}):null,objects(c)]));
var what=[el('p',{text:c.recommendation||'Recomendação não congelada nesta fotografia.'})];
if(c.expectedConfiguration)what.push(el('div',{cls:'kv'},[el('span',{cls:'k',text:'Configuração esperada'}),el('span',{text:c.expectedConfiguration})]));
var docs=c.references.filter(function(r){return!r.isFramework;});if(docs.length){var ul=el('ul');docs.forEach(function(r){ul.appendChild(el('li',null,[link(r.url,r.code),' (',r.framework,')']));});what.push(el('div',null,[el('div',{cls:'k',text:'Documentação oficial'}),ul]));}
if(c.actions.length){var at=el('tbody');c.actions.forEach(function(a){at.appendChild(el('tr',null,[el('td',{text:a.title}),el('td',{text:a.status+(a.wasOverdue?' (em atraso)':'')}),el('td',{text:a.dueDate||'—'}),el('td',{text:a.nextStep}),el('td',{text:a.validationOutcome?a.validationOutcome+' em '+dt(a.validatedAt):'sem validação no ciclo'})]));});
what.push(el('div',{cls:'tw'},[el('table',null,[el('thead',null,[el('tr',null,['Plano de ação','Etapa','Prazo','Próxima providência','Validação'].map(function(x){return el('th',{text:x});}))]),at])]));}
else if(c.status==='Exposed'||c.status==='Mitigated')what.push(el('p',{cls:'sub',text:'Nenhum plano de ação registrado para este controle no momento da publicação.'}));
b.appendChild(sec('O que fazer',what));
var fw=c.references.filter(function(r){return r.isFramework;}).map(function(r){return r.framework+(r.version?' '+r.version:'')+': '+r.code;});
b.appendChild(sec('Evidência técnica e proveniência',[el('div',{cls:'kv'},[el('span',{cls:'k',text:'Plataforma · serviço · domínio'}),el('span',{text:(c.platform||c.provider)+' · '+c.service+' · '+c.domainLabel}),
el('span',{cls:'k',text:'Coletado em'}),el('span',{text:dt(c.collectedAt)}),el('span',{cls:'k',text:'Critério da regra'}),el('span',{text:c.criterion||'não exibido (catálogo da avaliação difere do catálogo dos textos, ou fotografia v1)'}),
el('span',{cls:'k',text:'Frameworks e benchmarks'}),el('span',{text:fw.length?fw.join(' · '):'—'}),el('span',{cls:'k',text:'Capacidades de coleta usadas'}),el('span',{cls:'mono',text:c.requiredCapabilities.join(', ')||'—'}),
el('span',{cls:'k',text:'Contribuição para a nota'}),el('span',{text:c.factor===null?'Fora da nota (não avaliado, erro ou não aplicável) — reduz a cobertura, não a nota.':'Peso '+c.weight+' × fator '+String(c.factor).replace('.',',')+' = '+String(c.achieved).replace('.',',')+' de '+c.possible+' ponto(s).'})])]));
return b;}
function objects(c){var wrap=el('div');var aff=c.objects.filter(function(o){return o.relation==='Affected';}),ev=c.objects.filter(function(o){return o.relation!=='Affected';});
var info=[];if(c.affectedCount>0)info.push('Afetados segundo a regra: '+(c.affectedComposition||c.affectedCount));if(c.evidenceCount>0)info.push(c.evidenceCount+' evidência(s) de configuração');
if(c.detailPreserved===null)wrap.appendChild(el('p',{cls:'note',text:'Esta fotografia (v1) não congelou os itens. A lista de hoje não serve de prova para um resultado anterior.'}));
else if(!c.objects.length)wrap.appendChild(el('p',{cls:'empty',text:c.affectedCount>0?'Itens não preservados para este controle.':'Nenhum item associado a este resultado.'}));
if(c.detailLimitation)wrap.appendChild(el('p',{cls:'note',text:c.detailLimitation}));
if(!c.objects.length)return wrap;wrap.appendChild(el('p',{cls:'sub',text:info.join(' · ')}));
var all=aff.concat(ev),shown=50,tb=el('tbody'),filter='';
var q=all.length>10?el('input',{type:'search',placeholder:'Filtrar itens deste controle…','aria-label':'Filtrar itens de '+c.id,oninput:function(){filter=q.value.toLowerCase();draw();}}):null;
var more=el('button',{cls:'btn no-print',type:'button',onclick:function(){shown=all.length;draw();}});
function draw(){tb.textContent='';var rows=all.filter(function(o){return!filter||[o.externalId,o.displayName||'',o.userPrincipalName||'',o.detail||''].join(' ').toLowerCase().indexOf(filter)>=0;});
rows.slice(0,shown).forEach(function(o){tb.appendChild(el('tr',null,[el('td',null,[pill(o.relation==='Affected'?'s-Exposed':'s-NotEvaluated',o.relationLabel)]),el('td',{text:o.kindLabel}),
el('td',null,[el('div',{text:o.displayName||o.userPrincipalName||o.externalId}),el('div',{cls:'mono',text:(o.userPrincipalName&&o.displayName?o.userPrincipalName+' · ':'')+o.externalId}),o.roles.length?el('div',{cls:'mono',text:'Papéis: '+o.roles.join(', ')}):null]),
el('td',{text:o.detail||'—'}),el('td',{cls:'mono',text:o.observedConfiguration||'—'})]));});
more.textContent='Mostrar todos ('+rows.length+')';more.hidden=rows.length<=shown;}
if(q)wrap.appendChild(q);wrap.appendChild(el('div',{cls:'tw'},[el('table',null,[el('thead',null,[el('tr',null,['Relação','Tipo','Item','Por que está aqui','Configuração encontrada'].map(function(x){return el('th',{text:x});}))]),tb])]));wrap.appendChild(more);draw();return wrap;}
// ---------- Montagem ----------
var tabs=el('div',{cls:'tabs',role:'tablist','aria-label':'Seções do relatório'});
[['overview','Visão geral'],['controls','Controles e findings']].forEach(function(t,i){tabs.appendChild(el('button',{id:'t-'+t[0],type:'button',role:'tab','data-tab':t[0],'aria-controls':'tab-'+t[0],'aria-selected':i===0?'true':'false',text:t[1],
onclick:function(){setTab(t[0]);},onkeydown:function(e){if(e.key==='ArrowRight'||e.key==='ArrowLeft'){var n=t[0]==='overview'?'controls':'overview';setTab(n);document.getElementById('t-'+n).focus();e.preventDefault();}}}));});
main.appendChild(tabs);main.appendChild(overview());main.appendChild(controlsTab());renderList();setTab('overview');
var ns=document.getElementById('nojs');if(ns)ns.hidden=true;
})();
""";
}
