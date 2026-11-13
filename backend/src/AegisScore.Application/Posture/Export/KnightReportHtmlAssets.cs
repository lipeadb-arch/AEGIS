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
--ok:#006b7d;--ok-bg:#e3f6fa;--fail:#c2185b;--fail-bg:#fce4ec;--warn:#9a5b00;--warn-bg:#fff4e0;--ne:#56627a;--ne-bg:#eef1f6;--na:#b7c0cf;--err:#6b3fa0;--err-bg:#f1eafa;
--crit:#8e1b12;--high:#c2410c;--med:#a16207;--low:#1d5fb8;--info:#56627a;font-family:Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:var(--ink);}
@media (prefers-color-scheme:dark){:root{--bg:#0d1422;--panel:#141d2e;--ink:#e7ecf5;--ink2:#b8c2d6;--muted:#8e9ab2;--line:#26324a;--accent:#38c5ec;--accent-bg:#0f2a38;
--ok:#26e0ff;--ok-bg:#0e3440;--fail:#ff5c8a;--fail-bg:#3a1424;--warn:#f0b24a;--warn-bg:#35270d;--ne:#a6b1c6;--ne-bg:#1d2638;--na:#4a5468;--err:#c7a6f5;--err-bg:#2a1f3d;
--crit:#ff8a80;--high:#ff9f5a;--med:#f0c04a;--low:#7fb2ff;--info:#a6b1c6;}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);line-height:1.5;font-size:14px}
a{color:var(--accent)}a:focus-visible,button:focus-visible,input:focus-visible,select:focus-visible{outline:3px solid var(--accent);outline-offset:2px}
.wrap{max-width:1180px;margin:0 auto;padding:20px 16px 48px}
header.top{background:var(--panel);border-bottom:3px solid var(--accent);}
header.top .wrap{padding-top:18px;padding-bottom:14px}
.brand{font-size:12px;letter-spacing:.14em;text-transform:uppercase;color:var(--accent);font-weight:700}
h1{margin:4px 0 2px;font-size:24px}h2{font-size:17px;margin:0 0 10px}h3{font-size:14px;margin:14px 0 6px}
.sub{color:var(--ink2);margin:0}.meta{color:var(--muted);font-size:12px;margin-top:6px;display:flex;flex-wrap:wrap;gap:4px 14px}
.tabs{display:flex;flex-wrap:wrap;gap:4px;margin:16px 0 0;border-bottom:1px solid var(--line)}
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
.seg{height:100%}.seg.Passed{background:var(--ok)}.seg.Exposed{background:var(--fail)}.seg.Mitigated{background:var(--warn)}.seg.NotEvaluated{background:var(--ne)}.seg.NotApplicable{background:var(--na)}.seg.Error{background:var(--err)}
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
.refs{list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:4px}
.ref{display:block;width:100%;text-align:left;background:none;border:1px solid var(--line);border-radius:6px;padding:4px 8px;font:inherit;color:var(--ink);cursor:pointer}
.ref:hover{border-color:var(--accent)}.ref .rt{display:block}.ref .rc{display:block;font-family:Consolas,Menlo,monospace;font-size:11px;color:var(--muted)}
.linkbtn{background:none;border:0;padding:4px 0;font:inherit;font-size:13px;color:var(--accent);text-decoration:underline;cursor:pointer}
.gl dt{font-weight:700}.gl dd{margin:0 0 8px}.gl .mean{color:var(--ink2)}.gid{border-top:1px solid var(--line);padding:8px 0}
.gfind{display:flex;flex-direction:column;gap:3px;font-size:12px;color:var(--muted);max-width:420px}
.gfind input{font:inherit;padding:7px 8px;border:1px solid var(--line);border-radius:8px;background:var(--panel);color:var(--ink)}
@media (max-width:600px){table.stack thead{display:none}table.stack tr{display:block;border-bottom:1px solid var(--line);padding:6px 0}
table.stack td{display:block;border:0;padding:3px 0}table.stack td[data-l]::before{content:attr(data-l);display:block;font-size:11px;color:var(--muted);text-transform:uppercase;letter-spacing:.04em}
.tabs button{padding:8px 10px}}
.empty{color:var(--muted);font-style:italic}footer{color:var(--muted);font-size:12px;margin-top:24px;overflow-wrap:anywhere}
.grp{margin-top:22px}.grp-h{font-size:19px;margin:0 0 8px;padding-bottom:6px;border-bottom:2px solid var(--line)}.muted{color:var(--muted)}
.incl{margin:0 0 10px}.kpi .v small{font-size:.5em;color:var(--muted);margin-left:2px}.kpi.partial{border-top:3px solid var(--warn)}
.lims,.prio{margin:6px 0 0;padding-left:20px;display:flex;flex-direction:column;gap:10px}.lims{list-style:none;padding-left:0}
.lims li,.prio li{border-bottom:1px solid var(--line);padding-bottom:10px}.lims li:last-child,.prio li:last-child{border-bottom:0}
.prio-hd{display:flex;flex-wrap:wrap;gap:6px;align-items:baseline}.prio .meta{color:var(--muted);font-size:12px;margin:2px 0 6px;overflow-wrap:anywhere}
.pdl{display:grid;grid-template-columns:110px minmax(0,1fr);gap:4px 12px;margin:0;font-size:13px}.pdl dt{color:var(--muted)}.pdl dd{margin:0;overflow-wrap:anywhere}
.tech{font-size:12px;color:var(--muted)}.tech summary{cursor:pointer}.tech ul{margin:4px 0 0;padding-left:16px}
@media (max-width:600px){.pdl{grid-template-columns:minmax(0,1fr)}.pdl dd{margin-bottom:4px}}
@media (prefers-color-scheme:dark){.chart .in-ok,.chart .in-bad{fill:#0d1422}}
.tabs[hidden]{display:none}.srcnav{margin-top:10px;border-bottom:2px solid var(--line)}.srcnav button{font-size:15px}.src-meta{margin:0 0 10px}
.s-Compliant{color:var(--ok);background:var(--ok-bg)}.s-NonCompliant{color:var(--fail);background:var(--fail-bg)}.s-OutOfScope{color:var(--ne);background:var(--panel);border:1px dashed var(--ne)}
.cisbar{display:flex;height:16px;border-radius:4px;overflow:hidden;background:var(--ne-bg);margin:10px 0 6px}.cisbar span{height:100%}
.c-Compliant{background:var(--ok)}.c-NonCompliant{background:var(--fail)}.c-NotEvaluated{background:var(--ne)}.c-NotApplicable{background:var(--na)}
.c-OutOfScope{background:repeating-linear-gradient(45deg,var(--na),var(--na) 3px,var(--panel) 3px,var(--panel) 6px)}
.cis-legend{list-style:none;margin:0;padding:0;display:flex;flex-wrap:wrap;gap:4px 12px;font-size:12px}.cis-legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:4px;vertical-align:-1px}
.bm h3{margin-top:0}.bm .kv{margin-top:8px;grid-template-columns:150px minmax(0,1fr)}.cisf{grid-template-columns:2fr repeat(4,minmax(0,1fr)) auto}
.cis-sec{font-family:Consolas,Menlo,monospace;font-weight:700;margin-right:6px}.cis-rules td{font-size:12px}.lgrp li{margin-bottom:4px}
@media (max-width:980px){.cisf{grid-template-columns:repeat(2,minmax(0,1fr))}}@media (max-width:700px){.bm .kv{grid-template-columns:minmax(0,1fr)}}
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
function setTab(t){ST.tab=t;var b=document.querySelectorAll('.tabs button[data-tab]');for(var i=0;i<b.length;i++){var on=b[i].getAttribute('data-tab')===t;b[i].setAttribute('aria-selected',on?'true':'false');b[i].tabIndex=on?0:-1;}
['overview','cis','controls','glossary'].forEach(function(x){document.getElementById('tab-'+x).hidden=t!==x;});}
// [AEGIS-KNIGHT-PRESENTATION-01] Controle citado fora do detalhe: título CONGELADO na fotografia como informação principal,
// código como secundária; clique, Enter ou Espaço abrem o detalhe. Sem título na fotografia, o código fica e isso é dito.
var CT={};D.controls.forEach(function(c){CT[c.id]=c;});
var GL={};(D.glossary||[]).forEach(function(g){GL[g.term]=g;});
var uid=0;
function ctlRef(id,title){var t=title||(CT[id]&&CT[id].title)||null;
return el('button',{cls:'ref',type:'button','aria-label':(t?t+' ('+id+')':'Controle '+id)+': abrir o detalhe',onclick:function(){go({},id);}},[el('span',{cls:'rt',text:t||'Título não disponível nesta fotografia'}),el('span',{cls:'rc',text:id})]);}
function ctlRefs(refs,preview){var ul=el('ul',{cls:'refs',id:'refs-'+(++uid)}),extra=[];
refs.forEach(function(r,i){var li=el('li',null,[ctlRef(r.id,r.title)]);if(i>=preview){li.hidden=true;extra.push(li);}ul.appendChild(li);});
var box=el('div',null,[ul]);if(!extra.length)return box;
var more='Mostrar todos os '+refs.length+' controles',less='Mostrar só os '+preview+' primeiros';
var b=el('button',{cls:'linkbtn no-print',type:'button','aria-expanded':'false','aria-controls':ul.id,text:more,onclick:function(){var o=b.getAttribute('aria-expanded')==='true';
extra.forEach(function(li){li.hidden=o;});b.setAttribute('aria-expanded',o?'false':'true');b.textContent=o?more:less;if(!o){var f=extra[0].querySelector('button');if(f)f.focus();}}});
box.appendChild(b);return box;}
function refsOf(ids){return ids.map(function(id){return{id:id,title:null};});}
function go(filters,open){if(srcSel!=='geral')setSrc('geral');ST.f={q:'',status:'',severity:'',platform:'',service:'',domain:'',framework:''};for(var k in filters)ST.f[k]=filters[k];syncFilterInputs();renderList(open);setTab('controls');
var h=document.getElementById('tab-controls');if(open){var c=document.getElementById('ctl-'+open);if(c){c.scrollIntoView();var b=c.querySelector('button');if(b)b.focus();}}else h.scrollIntoView();}
// ---------- Visão geral ----------
function help(t,body){return el('details',null,[el('summary',{text:t}),el('span',{text:body})]);}
// [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Primeira aba: resumo executivo, riscos e ações prioritárias (desenhados no servidor e legíveis
// sem JavaScript — aqui só são movidos), resultados e composição e, por fim, limitações detalhadas e interpretação (também do servidor). Detalhes técnicos e metodologia ficam nas outras abas.
function overview(){var k=D.kpis,box=el('div',{id:'tab-overview',role:'tabpanel','aria-labelledby':'t-overview'});
['aegis-summary','aegis-risks'].forEach(function(id){var s=document.getElementById(id);if(s)box.appendChild(s);});
// [AEGIS-KNIGHT-CIS-01] Ordem da leitura: resumo da avaliação, prioridades, resultados por fonte e, por fim, limitações.
if(D.composition&&D.composition.length)box.appendChild(el('section',{cls:'grp','aria-labelledby':'bysrc-h'},[el('h2',{id:'bysrc-h',cls:'grp-h',text:'Resultados por fonte'}),composition()]));
var res=el('section',{cls:'grp','aria-labelledby':'res-h'},[el('h2',{id:'res-h',cls:'grp-h',text:'Distribuição dos resultados'})]);
// [AEGIS-ASSESSMENT-VISUALS-01] Painel visual congelado (desenhado no servidor): substitui as barras de severidade, domínio e serviço —
// a mesma distribuição não aparece duas vezes.
var VIS=document.getElementById('aegis-visuals');if(VIS)res.appendChild(VIS);
else{var two=el('div',{cls:'grid g2'});
var sp=el('div',{cls:'panel'},[el('h2',{text:'Controles com achados por severidade'}),el('p',{cls:'sub',text:'Controles reprovados ou mitigados. Clique para ver a lista.'})]),sb=el('div',{cls:'bars'});
var maxS=Math.max.apply(null,k.findingsBySeverity.map(function(x){return x.count;}).concat([1]));
k.findingsBySeverity.forEach(function(x){var tr=el('span',{cls:'track'}),s=el('span',{cls:'seg sev-'+x.key});s.style.width=(100*x.count/maxS)+'%';tr.appendChild(s);
sb.appendChild(el('button',{cls:'bar',type:'button','aria-label':x.label+': '+x.count+' controle(s) com achado',onclick:function(){go({status:'findings',severity:x.key});}},[el('span',{cls:'lbl'},[pill('v-'+x.key,x.label)]),tr,el('span',{cls:'num',text:String(x.count)})]));});
sp.appendChild(sb);two.appendChild(sp);two.appendChild(dist('Resultado por serviço',D.byService,'service'));res.appendChild(two);}
if(D.byPlatform&&D.byPlatform.length>1)res.appendChild(dist('Resultado por plataforma',D.byPlatform,'platform'));
var to=el('div',{cls:'panel'},[el('h2',{text:'Contas, aplicativos e demais itens mais recorrentes'}),el('p',{cls:'sub',text:'Itens afetados (contas, aplicativos, grupos, papéis, políticas…) que aparecem em mais controles reprovados ou mitigados.'})]);
if(D.topObjects.length===0)to.appendChild(el('p',{cls:'empty',text:D.kpis.uniqueAffected===null?'Esta fotografia não congelou os itens afetados.':'Nenhum item afetado preservado.'}));
else{var tb=el('tbody'),H=['Conta ou recurso afetado','Tipo','Quantidade de controles','Controles relacionados'];D.topObjects.forEach(function(o){tb.appendChild(el('tr',null,[el('td',{'data-l':H[0]},[el('div',{text:o.label}),el('div',{cls:'mono',text:o.externalId})]),el('td',{'data-l':H[1],text:o.kindLabel}),el('td',{'data-l':H[2],text:String(o.controlCount)}),
el('td',{'data-l':H[3]},[ctlRefs(o.controls||refsOf(o.controlIds),3)])]));});
to.appendChild(el('p',{cls:'sub',text:'Cada controle relacionado mostra o título registrado nesta fotografia e o código; clique para abrir o detalhe.'}));
to.appendChild(el('div',{cls:'tw'},[el('table',{cls:'stack'},[el('thead',null,[el('tr',null,H.map(function(x){return el('th',{text:x});}))]),tb])]));}
res.appendChild(to);box.appendChild(res);
var lim=document.getElementById('aegis-limits');if(lim)box.appendChild(lim);
return box;}
// Contagens por resultado (detalhes técnicos): cada botão abre a lista já filtrada.
function countsPanel(){var k=D.kpis,cp=el('div',{cls:'panel'},[el('h2',{text:'Controles por resultado'})]),cs=el('div',{cls:'counts'});
[['Exposed',k.failed],['Mitigated',k.mitigated],['Error',k.errors],['NotEvaluated',k.notEvaluated],['Passed',k.passed],['NotApplicable',k.notApplicable]].forEach(function(x){
cs.appendChild(el('button',{cls:'cbtn',type:'button',onclick:function(){go({status:x[0]});}},[el('b',{text:String(x[1])}),pill('s-'+x[0],lbl(STATUS,x[0]))]));});
cp.appendChild(cs);cp.appendChild(el('p',{cls:'sub',text:k.totalControls+' controle(s) no escopo desta avaliação. Não avaliado: a evidência faltou, foi insuficiente ou inconclusiva (inclui dado ou permissão ausente) — o motivo aparece em cada controle; reduz a cobertura e nunca aprova. Erro na avaliação: a regra falhou ao avaliar um dado coletado (diferente de falha de coleta). Mitigado: exposição com controle compensatório comprovado.'}));
return cp;}
function dist(title,rows,key){var p=el('div',{cls:'panel'},[el('h2',{text:title}),el('p',{cls:'sub',text:'Clique para ver os controles. Cores sempre acompanhadas do rótulo na legenda.'})]),b=el('div',{cls:'bars'});
var max=Math.max.apply(null,rows.map(function(r){return r.passed+r.failed+r.mitigated+r.notEvaluated+r.errors+r.notApplicable;}).concat([1]));
rows.forEach(function(r){var tot=r.passed+r.failed+r.mitigated+r.notEvaluated+r.errors+r.notApplicable,tr=el('span',{cls:'track'});
[['Passed',r.passed],['Exposed',r.failed],['Mitigated',r.mitigated],['Error',r.errors],['NotEvaluated',r.notEvaluated],['NotApplicable',r.notApplicable]].forEach(function(x){if(!x[1])return;var s=el('span',{cls:'seg '+x[0],title:lbl(STATUS,x[0])+': '+x[1]});s.style.width=(100*x[1]/max)+'%';tr.appendChild(s);});
var f={};f[key]=r.key;var desc=r.label+': '+r.failed+' reprovado(s), '+r.passed+' aprovado(s), '+(r.notEvaluated+r.errors)+' não avaliado(s)/erro, de '+tot;
b.appendChild(el('button',{cls:'bar',type:'button','aria-label':desc,title:desc,onclick:function(){go(f);}},[el('span',{cls:'lbl',text:r.label}),tr,el('span',{cls:'num',text:String(tot)})]));});
p.appendChild(b);var lg=el('div',{cls:'legend'});STATUS.forEach(function(s){var i=el('i',{cls:'seg '+s[0]});lg.appendChild(el('span',null,[i,s[1]]));});p.appendChild(lg);return p;}
var SST={Completed:'Coleta concluída',PartialCollection:'Coleta parcial',InsufficientPermission:'Permissão insuficiente',AuthenticationFailure:'Falha de autenticação',Throttled:'Limite de requisições',Unavailable:'Indisponível',Error:'Erro'};
function composition(){var p=el('div',{cls:'panel'},[el('h3',{text:'Composição do relatório consolidado'}),
el('p',{cls:'sub',text:'Cada fonte tem a própria nota, cobertura e data — não são somadas. Uma fonte disponível e não incluída, ou sem avaliação concluída, aparece aqui sem virar aprovação. Uma fonte fora do escopo do tenant não entra em nenhum total deste relatório.'})]);
var tb=el('tbody'),H=['Fonte','Situação','Nota','Avaliados','Aprovados','Achados','Sem avaliação','Catálogo','Coleta','Limitações da fonte'];
D.composition.forEach(function(s){var sit=s.included?['s-Passed','Incluída']:(s.availabilityState==='NotAssessed'?['s-NotEvaluated','Sem avaliação concluída']:s.availabilityState==='OutOfScope'?['s-NotApplicable','Fora do escopo']:['s-Mitigated','Disponível, não incluída']);
var has=s.passedCount!==null&&s.passedCount!==undefined,ev=has?s.passedCount+s.exposedCount+s.mitigatedCount:0,ap=has?ev+s.notEvaluatedCount+s.errorCount:0;
tb.appendChild(el('tr',null,[el('td',{'data-l':H[0],text:s.label}),el('td',{'data-l':H[1]},[pill(sit[0],sit[1])]),
el('td',{'data-l':H[2],text:s.score===null||s.score===undefined?'—':String(Math.round(s.score))}),
el('td',{'data-l':H[3],text:has?ev+' de '+ap+' ('+pct(s.coverage)+')':'—'}),el('td',{'data-l':H[4],text:has?String(s.passedCount):'—'}),
el('td',{'data-l':H[5],text:has?String(s.exposedCount+s.mitigatedCount):'—'}),el('td',{'data-l':H[6],text:has?String(s.notEvaluatedCount+s.errorCount):'—'}),
el('td',{'data-l':H[7],text:s.catalogVersion||'—'}),el('td',{'data-l':H[8],text:has?dt(s.capturedAt)+(s.sourceState?' · '+(SST[s.sourceState]||s.sourceState):''):'—'}),
el('td',{'data-l':H[9]},[limGroups(s.collectionLimitations||[])])]));});
if(D.header.collectionSummary)p.appendChild(el('p',{cls:'sub',text:D.header.collectionSummary}));
p.appendChild(el('div',{cls:'tw'},[el('table',{cls:'stack'},[el('thead',null,[el('tr',null,H.map(function(x){return el('th',{text:x});}))]),tb])]));
p.appendChild(el('p',{cls:'sub',text:'Avaliados = aprovados + achados (reprovados e mitigados) sobre os controles aplicáveis da fonte; os não aplicáveis ficam fora da base.'}));
return p;}
// Causas repetidas agrupadas (ex.: "Autorização recusada · 5 leituras"); a frase de cada leitura fica recolhida.
function limGroups(list){if(!list.length)return el('span',{text:'—'});var g={},order=[];list.forEach(function(x){var i=x.indexOf(': '),cap=i>0?x.slice(0,i):x,rest=i>0?x.slice(i+2):'',j=rest.indexOf(' — '),cause=j>=0?rest.slice(0,j):(rest||'Limitação registrada');
if(!g[cause]){g[cause]=[];order.push(cause);}g[cause].push({cap:cap,detail:j>=0?rest.slice(j+3):''});});
var ul=el('ul',{cls:'lgrp'});order.forEach(function(c){var it=g[c];ul.appendChild(el('li',null,[el('strong',{text:c}),' · '+(it.length===1?'1 leitura':it.length+' leituras'),
el('details',{cls:'tech'},[el('summary',{text:'Ver leituras e detalhes'}),el('ul',null,it.map(function(x){return el('li',null,[el('span',{text:x.cap}),x.detail?el('div',{cls:'mono',text:x.detail}):null]);}))])]));});return ul;}
function sources(){var h=D.header,p=el('div',{cls:'panel'},[el('h2',{text:'Fontes, datas e limitações de coleta'})]);
p.appendChild(el('div',{cls:'kv'},[el('span',{cls:'k',text:'Fonte'}),el('span',{text:h.sourceLabel+' · provedor '+h.provider+(h.isDemo?' (demonstração sintética)':'')}),
el('span',{cls:'k',text:'Coleta mais recente'}),el('span',{text:dt(h.dataRecency)}),
h.collectionStateLabel?el('span',{cls:'k',text:'Estado da coleta'}):null,h.collectionStateLabel?el('span',{text:h.collectionStateLabel+' — '+h.collectionSummary}):null,el('span',{cls:'k',text:'Fotografia publicada em'}),el('span',{text:dt(h.capturedAt)}),
el('span',{cls:'k',text:'Escopo desta avaliação'}),el('span',{text:'Controles avaliados a partir de '+h.sourceLabel+'. Serviços ainda não coletados não aparecem como avaliados; o que falta está na cobertura do catálogo de referência.'})]));
if(D.limitations.length===0&&D.legacyLimitations.length===0)p.appendChild(el('p',{cls:'sub',text:D.noLimitationText||'Não há limitações de coleta registradas nesta fotografia.'}));
if(D.limitations.length){var tb=el('tbody');D.limitations.forEach(function(l){tb.appendChild(el('tr',null,[el('td',{text:l.capabilityLabel}),el('td',{text:l.causeLabel}),
el('td',null,[el('div',{text:l.detail||'—'}),l.requiredPermission?el('div',{cls:'mono',text:'Permissão exigida pela chamada implementada: '+l.requiredPermission}):null,
(l.technical&&l.technical.length)?el('details',{cls:'tech'},[el('summary',{text:'Detalhe técnico'}),el('ul',null,l.technical.map(function(t){return el('li',{cls:'mono',text:t});}))]):null]),
el('td',null,[l.affectedControls.length?ctlRefs(refsOf(l.affectedControls),3):el('span',{text:'nenhum controle ficou sem avaliação por isso'})]),el('td',{text:l.guidance}),el('td',{text:dt(l.attemptedAt)})]));});
p.appendChild(el('h3',{text:'Limitações de coleta'}));p.appendChild(el('div',{cls:'tw'},[el('table',null,[el('thead',null,[el('tr',null,['Capacidade','Causa','Detalhe','Controles prejudicados','O que fazer','Tentativa'].map(function(x){return el('th',{text:x});}))]),tb])]));}
if(D.legacyLimitations.length){p.appendChild(el('h3',{text:'Limitações de coleta (registro da fotografia)'}));var ul=el('ul');D.legacyLimitations.forEach(function(x){ul.appendChild(el('li',{text:x}));});p.appendChild(ul);}
return p;}
function integrity(){var h=D.header,p=el('div',{cls:'panel'},[el('h2',{text:'Integridade, versões e reconciliação'})]);
p.appendChild(el('div',{cls:'kv'},[el('span',{cls:'k',text:'Fotografia'}),el('span',{cls:'mono',text:h.snapshotId}),el('span',{cls:'k',text:'Avaliação de origem'}),el('span',{cls:'mono',text:h.runId||'não registrada nesta fotografia'}),
el('span',{cls:'k',text:'Hash do conteúdo (SHA-256)'}),el('span',{cls:'mono',text:h.contentHash+(h.integrityVerified?' · verificado na exportação':'')}),
el('span',{cls:'k',text:'Versões'}),el('span',{cls:'mono',text:'schema '+h.schemaVersion+' · '+(h.sourceType==='Consolidated'&&D.composition?'catálogo por fonte: '+(D.composition.filter(function(s){return s.included;}).map(function(s){return s.label+' '+(s.catalogVersion||'não registrado');}).join(', ')||'nenhuma fonte incluída'):'catálogo '+h.catalogVersion)+' · fórmula '+h.formulaVersion+(h.profileCatalogVersion?' · textos '+h.profileCatalogVersion:'')})]));
p.appendChild(el('p',{cls:'sub',text:'Este arquivo contém a avaliação completa (todos os controles e itens congelados), não um recorte: os filtros da aba de controles afetam só a visualização. O CSV da mesma fotografia tem uma linha por item de cada controle (ou uma linha para o controle sem itens): nas linhas da avaliação automatizada (Origin "Automatizado"), controles = valores distintos de IndicatorId; ocorrências = linhas com ObjectRelation "Afetado" em controles reprovados ou mitigados; itens únicos = pares distintos (ObjectType, ObjectExternalId) dessas linhas. Linhas de verificação manual, do painel e do histórico têm Origin próprio.'}));
D.notes.forEach(function(n){p.appendChild(el('p',{cls:'note',text:n}));});return p;}
function coverage(){var c=D.referenceCoverage,p=el('div',{cls:'panel'},[el('h2',{text:'Cobertura do catálogo de referência'}),
el('p',{cls:'sub',text:'Três medidas diferentes, que não se somam: (1) cobertura do catálogo — o que o AEGIS consegue avaliar, propriedade do produto; (2) cobertura desta avaliação — o que a coleta conseguiu avaliar neste ambiente ('+pct(D.kpis.coverage)+'); (3) aprovação — o que foi avaliado e está conforme ('+pct(D.kpis.approvalPercent)+').'}),
el('p',{cls:'sub',text:c.frameworks.join(' · ')+' · catálogo '+c.catalogVersion+'. Integral = critério da referência; parcial = critério equivalente, não idêntico. Sem método na API oficial, verificação manual e acesso que o conector não tem nunca contam como avaliados. Leituras em versão preview são usadas e identificadas em cada controle: '+String(c.total.previewBacked||0)+' das referências avaliadas dependem delas.'})]);
var tb=el('tbody');[c.total].concat(c.byPlatform).forEach(function(r){tb.appendChild(el('tr',null,[el('td',{text:r.label}),el('td',{text:String(r.total)}),
el('td',{text:r.implemented+' ('+pct(r.fullPercent)+')'}),el('td',{text:r.partial+' ('+pct(r.partialPercent)+')'}),el('td',{text:String(r.pending)}),el('td',{text:String(r.previewBacked||0)}),el('td',{text:String(r.apiLimitation)}),el('td',{text:String(r.manualOnly)}),el('td',{text:String(r.requiresAccess)})]));});
p.appendChild(el('div',{cls:'tw'},[el('table',{cls:'cov'},[el('thead',null,[el('tr',null,['Recorte','Total','Integral','Parcial','Pendente','Depende de preview','Sem método na API','Manual','Outro acesso'].map(function(x){return el('th',{text:x});}))]),tb])]));
p.appendChild(el('p',{cls:'sub',text:'“Com alguma avaliação automatizada” (integral + parcial): '+pct(c.total.anyAutomatedPercent)+' — não é cobertura completa.'}));
var m=c.manualResults||[];p.appendChild(el('h3',{text:'Resultados de verificação manual (atestação)'}));
if(!m.length){p.appendChild(el('p',{cls:'sub',text:'Nenhum resultado manual registrado até a publicação.'}));return p;}
p.appendChild(el('p',{cls:'sub',text:'Registrados pela organização para controles sem avaliação automatizada ou, como complemento, para leituras automatizadas parciais (o indicador automatizado mantém o próprio resultado). Não entram na nota, na cobertura desta avaliação nem na aprovação.'}));
var mb=el('tbody');m.forEach(function(r){mb.appendChild(el('tr',null,[el('td',null,[el('strong',{text:r.title}),el('br'),el('span',{cls:'sub',text:r.referenceLabel+' · '+r.dispositionLabel})]),
el('td',{text:r.resultLabel+(r.expired?' — VENCIDO':'')}),el('td',{text:r.justification}),el('td',{text:r.responsibleName}),
el('td',{text:[r.evidenceReference,r.evidenceDocumentTitle?('Documento: '+r.evidenceDocumentTitle+(r.evidenceDocumentSha256?' (SHA-256 '+r.evidenceDocumentSha256.slice(0,12)+'…)':'')):null].filter(function(x){return x;}).join(' · ')||'—'}),
el('td',{text:(r.recordedByName||'—')+' em '+r.recordedAt.slice(0,10)+(r.validUntil?' · válido até '+r.validUntil:'')})]));});
p.appendChild(el('div',{cls:'tw'},[el('table',{cls:'cov'},[el('thead',null,[el('tr',null,['Controle de referência','Resultado','Justificativa','Responsável','Evidência','Registro'].map(function(x){return el('th',{text:x});}))]),mb])]));
return p;}
// ---------- [AEGIS-KNIGHT-CIS-01] CIS Benchmarks ----------
// Tudo vem da visão CONGELADA na publicação (D.cis): resultado por recomendação, regras e recursos que o sustentam e o mapeamento.
var CS=[['NonCompliant','Não conforme'],['NotEvaluated','Não avaliado'],['Compliant','Conforme'],['NotApplicable','Não aplicável'],['OutOfScope','Fora do escopo']];
var CF={q:'',bench:'',service:'',status:'',profile:''},cisBox=null,cisRec=null,cisIn={},RULE={};
function cnt(c,k){return k==='Compliant'?c.compliant:k==='NonCompliant'?c.nonCompliant:k==='NotEvaluated'?c.notEvaluated:k==='NotApplicable'?c.notApplicable:c.outOfScope;}
function thead(h){return el('thead',null,[el('tr',null,h.map(function(x){return el('th',{text:x});}))]);}
function kvl(rows){var d=el('div',{cls:'kv'});rows.forEach(function(r){if(r[1]===null||r[1]===undefined||r[1]==='')return;d.appendChild(el('span',{cls:'k',text:r[0]}));d.appendChild(typeof r[1]==='string'?el('span',{text:r[1]}):r[1]);});return d;}
function segBar(c,label){var tot=c.compliant+c.nonCompliant+c.notEvaluated+c.notApplicable+c.outOfScope,b=el('div',{cls:'cisbar',role:'img','aria-label':label+': '+CS.map(function(s){return s[1]+' '+cnt(c,s[0]);}).join(', ')});
CS.forEach(function(s){var n=cnt(c,s[0]);if(!n||!tot)return;var x=el('span',{cls:'c-'+s[0],title:s[1]+': '+n});x.style.width=(100*n/tot)+'%';b.appendChild(x);});return b;}
function bmCard(b){var c=b.counts,cons=c.compliant+c.nonCompliant+c.notEvaluated+c.notApplicable,ev=c.compliant+c.nonCompliant,m=b.manualCounts,man=m.compliant+m.nonCompliant+m.notApplicable;
var p=el('div',{cls:'panel bm'},[el('h3',{text:b.label}),el('p',{cls:'sub',text:b.recommendations+' recomendações · '+b.catalogEntries+' entradas do catálogo (variantes contam na recomendação) · '+cons+' consideradas'+(c.outOfScope?' · '+c.outOfScope+' fora do escopo':'')})]);
p.appendChild(segBar(c,b.label));var ul=el('ul',{cls:'cis-legend'});
CS.forEach(function(s){var n=cnt(c,s[0]);ul.appendChild(el('li',null,[el('i',{cls:'c-'+s[0]}),el('button',{type:'button',cls:'linkbtn','aria-label':b.label+': '+s[1]+', '+n+' — ver a lista',onclick:function(){cisGo({bench:b.key,status:s[0]});},text:s[1]+': '+n})]));});p.appendChild(ul);
p.appendChild(kvl([['Conformes',ev?c.compliant+' de '+ev+' com veredito ('+pct(100*c.compliant/ev)+')':'nenhuma recomendação com veredito'],['Com veredito',cons?ev+' de '+cons+' consideradas ('+pct(100*ev/cons)+')':'nenhuma considerada'],
['Verificação manual',man?man+' (conforme '+m.compliant+' · não conforme '+m.nonCompliant+' · não aplicável '+m.notApplicable+')':'nenhuma registrada'],['Não conformes mitigadas',b.mitigated?String(b.mitigated)+' (mitigação não é conformidade)':null],
['Perfil (rastreio)',b.profileNotRecorded+b.profileDivergent?(b.profileNotRecorded?b.profileNotRecorded+' sem perfil registrado':'')+(b.profileDivergent?(b.profileNotRecorded?' · ':'')+b.profileDivergent+' divergente(s)':'')+' na fonte de rastreio':'registrado em todas']]));
p.appendChild(el('p',{cls:'sub'},[link(b.url,'Página do benchmark no CIS'),' · edição '+b.version]));return p;}
function benchLabel(k){var b=(D.cis.benchmarks||[]).filter(function(x){return x.key===k;})[0];return b?b.label:k;}
function cisTab(){var box=el('div',{id:'tab-cis',role:'tabpanel','aria-labelledby':'t-cis'});box.hidden=true;var C=D.cis;
box.appendChild(el('h2',{cls:'grp-h',text:'CIS Benchmarks'}));
if(!C){box.appendChild(el('div',{cls:'panel'},[el('p',{text:D.cisState==='SingleSource'?'A visão CIS Benchmarks é publicada no relatório consolidado, que compõe as fontes do escopo. Este relatório é de uma fonte só.':D.cisState==='Frozen'?'A visão CIS registrada nesta fotografia não pôde ser lida.':'Esta publicação é anterior à visão CIS Benchmarks e não registrou resultados por recomendação. Os resultados do KNIGHT continuam valendo nas demais abas; uma nova publicação congela a visão CIS.'})]));return box;}
C.rules.forEach(function(r){RULE[r.indicatorId]=r;});
box.appendChild(el('p',{cls:'note info',text:C.disclaimer}));
var o=C.origin,op=el('div',{cls:'panel'},[el('h3',{text:'Avaliação de origem'}),kvl([['Composição',o.label],['Dados de',dt(o.dataRecency)],['Publicado em',dt(D.header.capturedAt)],['Mapeamento',C.mapping.note]])]);
var tb=el('tbody'),H=['Fonte','Situação','Coleta','Catálogo','Execução','Mapeamento'];
o.sources.forEach(function(s){tb.appendChild(el('tr',null,[el('td',{'data-l':H[0],text:s.label}),el('td',{'data-l':H[1]},[pill(s.included?'s-Passed':s.availability==='OutOfScope'?'s-OutOfScope':'s-NotEvaluated',s.availabilityLabel)]),
el('td',{'data-l':H[2],text:s.included?dt(s.capturedAt)+(s.sourceStateLabel?' · '+s.sourceStateLabel:''):'—'}),el('td',{'data-l':H[3],text:s.catalogVersion||'—'}),el('td',{'data-l':H[4],cls:'mono',text:s.runId||'—'}),
el('td',{'data-l':H[5],text:s.included?(s.mappingReliable?'Confirmado para esta versão':(s.note||'Não confirmado')):'—'})]));});
op.appendChild(el('div',{cls:'tw'},[el('table',{cls:'stack'},[thead(H),tb])]));box.appendChild(op);
var sg=el('div',{cls:'grid g2'});C.benchmarks.forEach(function(b){sg.appendChild(bmCard(b));});
box.appendChild(el('section',{cls:'grp','aria-labelledby':'cis-sum-h'},[el('h3',{id:'cis-sum-h',text:'Resumo por benchmark'}),el('p',{cls:'sub',text:'Cada recomendação conta uma vez no seu benchmark. Percentuais sobre as consideradas (Fora do escopo não entra). A nota KNIGHT ('+(D.kpis.score===null||D.kpis.score===undefined?'sem nota':Math.round(D.kpis.score)+'/100')+') é outra medida: pondera as regras do KNIGHT, não as recomendações, e não sai destes totais.'}),sg]));
var R=C.reconciliation,rp=el('div',{cls:'panel'},[el('h3',{text:'Por que os totais CIS diferem dos totais do KNIGHT'})]);
rp.appendChild(kvl([['KNIGHT',R.knightEvaluated+' de '+R.knightApplicable+' controles aplicáveis avaliados · '+R.knightNotApplicable+' não aplicáveis · '+R.knightControls+' no total'],['Regras na composição',R.rulesInComposition+' · '+R.rulesLinked+' sustentam recomendações CIS · '+R.ownChecks+' verificações próprias'],['Recomendações CIS',R.evaluated+' com veredito de '+R.considered+' consideradas · '+R.outOfScope+' fora do escopo · '+R.recommendations+' no total']]));
rp.appendChild(el('ul',null,R.explanation.map(function(x){return el('li',{text:x});})));box.appendChild(rp);
var f=el('div',{cls:'panel filters cisf',role:'search','aria-label':'Filtrar recomendações'});
function sel(name,label,opts){var s=el('select',{id:'cf-'+name,onchange:function(){CF[name]=s.value;if(name==='bench'){CF.profile='';fillProfiles();}cisRender();}},[el('option',{value:'',text:'Todos'})].concat(opts.map(function(o){return el('option',{value:o[0],text:o[1]});})));cisIn[name]=s;return el('label',{'for':'cf-'+name},[label,s]);}
var q=el('input',{id:'cf-q',type:'search',placeholder:'Seção (ex.: 5.2.2.1) ou título',oninput:function(){CF.q=q.value;cisRender();}});cisIn.q=q;
f.appendChild(el('label',{'for':'cf-q'},['Pesquisa',q]));f.appendChild(sel('bench','Benchmark',C.benchmarks.map(function(b){return[b.key,b.label];})));
f.appendChild(sel('service','Serviço',uniqPairs(C.recommendations.map(function(r){return[r.service,r.serviceLabel];}))));f.appendChild(sel('status','Resultado',CS));
f.appendChild(sel('profile','Perfil',[]));f.appendChild(el('button',{cls:'btn',type:'button',onclick:function(){cisGo({});},text:'Limpar filtros'}));
box.appendChild(f);cisRec=el('p',{cls:'recorte',role:'status','aria-live':'polite'});box.appendChild(cisRec);cisBox=el('div');box.appendChild(cisBox);
if(C.ownChecks.length){var ob=el('tbody');C.ownChecks.forEach(function(x){ob.appendChild(el('tr',null,[el('td',{'data-l':'Regra'},[ctlRef(x.indicatorId,x.title)]),el('td',{'data-l':'Fonte',text:x.sourceLabel}),el('td',{'data-l':'Resultado do KNIGHT'},[pill('s-'+x.status,x.statusLabel)]),el('td',{'data-l':'Por que fica fora',text:x.note})]));});
box.appendChild(el('details',{cls:'panel'},[el('summary',{text:'Verificações próprias do KNIGHT, fora dos totais CIS ('+C.ownChecks.length+')'}),el('p',{cls:'sub',text:'Regras do KNIGHT (inclusive de Entra ID) sem recomendação correspondente nos CIS Benchmarks registrados. Continuam na nota e nos resultados do KNIGHT.'}),el('div',{cls:'tw'},[el('table',{cls:'stack'},[thead(['Regra','Fonte','Resultado do KNIGHT','Por que fica fora']),ob])])]));}
box.appendChild(el('details',{cls:'panel'},[el('summary',{text:'Limitações da visão CIS'}),el('ul',null,C.limitations.map(function(x){return el('li',{text:x});}))]));
fillProfiles();cisRender();return box;}
function fillProfiles(){var s=cisIn.profile;if(!s)return;s.textContent='';s.appendChild(el('option',{value:'',text:'Todos os perfis'}));
var bs=D.cis.benchmarks.filter(function(b){return!CF.bench||b.key===CF.bench;}),seen={};bs.forEach(function(b){b.profiles.forEach(function(p){if(seen[p.key])return;seen[p.key]=1;s.appendChild(el('option',{value:p.key,text:p.label+(CF.bench?' ('+p.recommendations+')':'')}));});});
s.appendChild(el('option',{value:'-',text:'Perfil não registrado na fonte de rastreio'}));s.value=CF.profile||'';}
function cisMatch(r){var f=CF;if(f.bench&&r.benchmark!==f.bench)return false;if(f.service&&r.service!==f.service)return false;if(f.status&&r.status!==f.status)return false;
if(f.profile==='-'){if(r.profiles.length)return false;}else if(f.profile&&r.profiles.indexOf(f.profile)<0)return false;
if(f.q){var t=f.q.toLowerCase().trim();if((r.section+' '+r.title+' '+r.id).toLowerCase().indexOf(t)<0)return false;}return true;}
function cisGo(fl,open){if(srcSel!=='geral')setSrc('geral');CF={q:'',bench:'',service:'',status:'',profile:''};for(var k in fl)CF[k]=fl[k];fillProfiles();for(var k2 in cisIn)cisIn[k2].value=CF[k2]||'';cisRender(open);setTab('cis');
var h=document.getElementById(open?'cis-'+open:'tab-cis');if(h){h.scrollIntoView();var b=h.querySelector('button');if(b&&open)b.focus();}}
function cisRender(open){if(!cisBox)return;var all=D.cis.recommendations,list=all.filter(cisMatch);cisBox.textContent='';
var c={compliant:0,nonCompliant:0,notEvaluated:0,notApplicable:0,outOfScope:0};list.forEach(function(r){var k=r.status;c[k.charAt(0).toLowerCase()+k.slice(1)]++;});
var cons=c.compliant+c.nonCompliant+c.notEvaluated+c.notApplicable;
cisRec.textContent='Mostrando '+list.length+' de '+all.length+' recomendações · '+(c.compliant+c.nonCompliant)+' com veredito de '+cons+' consideradas no recorte ('+c.compliant+' conformes, '+c.nonCompliant+' não conformes, '+c.notEvaluated+' não avaliadas, '+c.notApplicable+' não aplicáveis'+(c.outOfScope?', '+c.outOfScope+' fora do escopo':'')+').';
if(!list.length){cisBox.appendChild(el('p',{cls:'empty',text:'Nenhuma recomendação corresponde aos filtros. Use “Limpar filtros”.'}));return;}
list.forEach(function(r){cisBox.appendChild(cisItem(r,open===r.id));});}
function cisItem(r,isOpen){var body=null,id='cis-'+r.id;
var head=el('button',{type:'button','aria-expanded':isOpen?'true':'false','aria-controls':id+'-b',onclick:function(){var o=head.getAttribute('aria-expanded')==='true';head.setAttribute('aria-expanded',o?'false':'true');if(!body){body=cisDetail(r);body.id=id+'-b';wrap.appendChild(body);}body.hidden=o;}},
[el('span',null,[el('div',{cls:'t'},[el('span',{cls:'cis-sec',text:r.section}),r.title]),el('div',{cls:'s',text:benchLabel(r.benchmark)+' · '+r.serviceLabel+(r.profileLabels.length?' · '+r.profileLabels.join(', '):'')+(r.ruleIds.length?' · regras '+r.ruleIds.join(', '):' · sem regra automatizada')})]),
el('span',{cls:'tags'},[pill('s-'+r.status,r.statusLabel+(r.mitigated?' (mitigada)':'')),r.origin==='Manual'||r.origin==='AutomatedAndManual'?pill('s-Mitigated',r.originLabel):null,pill('v-'+r.severity,r.severityLabel)])]);
var wrap=el('div',{cls:'ctl',id:id},[head]);if(isOpen){body=cisDetail(r);body.id=id+'-b';wrap.appendChild(body);}return wrap;}
function cisDetail(r){var b=el('div',{cls:'ctl-body'});
b.appendChild(sec('O que foi verificado',[el('p',{text:r.whatChecked}),kvl([['Recomendação',benchLabel(r.benchmark)+' · seção '+r.section],['Serviço',r.serviceLabel+' · '+r.platform],['Perfil',r.profileLabels.length?r.profileLabels.join(', '):r.profileStateLabel],['Severidade de referência',r.severityLabel],['Origem do resultado',r.originLabel]])]));
b.appendChild(sec('O que encontramos',[el('p',{text:r.found}),r.status==='NotEvaluated'||!r.reason?null:el('p',{cls:'sub',text:r.reason})]));
if(r.whyItMatters)b.appendChild(sec('Por que importa',[el('p',{text:r.whyItMatters})]));
var fix=[];if(r.howToFix)fix.push(el('p',{text:r.howToFix}));
if(r.links.length){var ul=el('ul');r.links.forEach(function(l){ul.appendChild(el('li',null,[link(l.url,l.title),' (Microsoft Learn)']));});fix.push(el('div',null,[el('div',{cls:'k',text:'Documentação oficial'}),ul]));}
if(r.status==='NonCompliant')fix.push(el('p',{cls:'sub',text:'Orientação para revisão humana: confirme o impacto na operação antes de alterar a configuração e valide pela próxima coleta.'}));
if(!fix.length)fix.push(el('p',{cls:'sub',text:r.status==='Compliant'?'Nada a corrigir: a evidência comprova o critério.':r.status==='NotApplicable'?'Nada a corrigir: o critério não se aplica a este ambiente.':r.status==='OutOfScope'?'A fonte desta recomendação foi retirada do escopo da avaliação.':'Sem orientação adicional registrada.'}));
b.appendChild(sec('Como corrigir',fix));
var rows=[];r.variants.forEach(function(v){v.rules.forEach(function(x){rows.push({v:v,x:x});});});
if(rows.length){var tb=el('tbody'),H=['Regra do KNIGHT','Resultado','Valor observado','Valor esperado','Contas e recursos','Limitação','O que prova'];
rows.forEach(function(o){var ru=RULE[o.x.indicatorId];tb.appendChild(el('tr',null,[el('td',{'data-l':H[0]},[ctlRef(o.x.indicatorId,ru?ru.title:null),el('div',{cls:'sub',text:(ru?ru.sourceLabel+' · ':'')+'vínculo '+o.x.matchLabel.toLowerCase()+(o.v.variant?' · variante '+o.v.variant:'')})]),
el('td',{'data-l':H[1]},[ru?pill('s-'+ru.status,ru.statusLabel):el('span',{text:'sem resultado'})]),el('td',{'data-l':H[2],text:ru?ru.evidence:'—'}),el('td',{'data-l':H[3],text:ru&&ru.expected?ru.expected:'—'}),
el('td',{'data-l':H[4],text:ru?((ru.affectedComposition||(ru.affectedCount?ru.affectedCount+' afetado(s)':'nenhum afetado'))+(ru.evidenceCount?' · '+ru.evidenceCount+' evidência(s) de configuração':'')):'—'}),
el('td',{'data-l':H[5],text:[ru&&ru.notEvaluatedReason,ru&&ru.detailLimitation,o.x.note].filter(function(x){return x;}).join(' · ')||'—'}),el('td',{'data-l':H[6],text:o.x.proofLabel})]));});
b.appendChild(sec('Regras e recursos que sustentam o resultado',[el('p',{cls:'sub',text:'O detalhe de cada regra (com a lista de contas e recursos) abre em Detalhes técnicos.'}),el('div',{cls:'tw'},[el('table',{cls:'stack cis-rules'},[thead(H),tb])])]));}
if(r.variants.length>1||r.variants[0].variant){var vb=el('tbody');r.variants.forEach(function(v){vb.appendChild(el('tr',null,[el('td',{'data-l':'Variante do catálogo',text:(v.variant||'—')+' · '+v.title}),el('td',{'data-l':'Resultado'},[pill('s-'+v.status,v.statusLabel)]),el('td',{'data-l':'Motivo',text:v.reason||'—'}),el('td',{'data-l':'Disposição',text:v.dispositionLabel})]));});
b.appendChild(sec('Variantes do catálogo nesta recomendação',[el('p',{cls:'sub',text:'As variantes verificam partes da mesma recomendação oficial; a recomendação conta uma vez.'}),el('div',{cls:'tw'},[el('table',{cls:'stack'},[thead(['Variante do catálogo','Resultado','Motivo','Disposição']),vb])])]));}
if(r.manual){var m=r.manual;b.appendChild(sec('Verificação manual',[kvl([['Resultado',m.resultLabel+(m.expired?' — VENCIDO':'')+(m.complement?' (complemento da leitura automatizada)':'')],['Justificativa',m.justification],['Responsável',m.responsibleName],['Evidência',[m.evidenceReference,m.evidenceDocumentTitle?'Documento: '+m.evidenceDocumentTitle+(m.evidenceDocumentSha256?' (SHA-256 '+m.evidenceDocumentSha256.slice(0,12)+'…)':''):null].filter(function(x){return x;}).join(' · ')||'—'],
['Registro',(m.recordedByName||'—')+' em '+dt(m.recordedAt)],['Validade',m.validUntil?'até '+m.validUntil+(m.expired?' (vencida)':''):'sem data de revisão']]),el('p',{cls:'sub',text:'Atestação registrada por pessoas: aparece com a origem manual e não entra na nota KNIGHT.'})]));}
if(r.limitations.length)b.appendChild(sec('Limitações',[el('ul',null,r.limitations.map(function(x){return el('li',{text:x});}))]));
var tr=r.variants.map(function(v){return v.traceRule;}).filter(function(x){return x;});
b.appendChild(el('details',{cls:'tech'},[el('summary',{text:'Identificadores e rastreio'}),el('ul',null,[el('li',{cls:'mono',text:'Recomendação: '+r.id}),el('li',{cls:'mono',text:'Entradas do catálogo: '+r.variants.map(function(v){return v.key;}).join(', ')}),
tr.length?el('li',{cls:'mono',text:'Regra de rastreio (Monkey365): '+tr.filter(function(x,i){return tr.indexOf(x)===i;}).join(', ')}):null,el('li',{cls:'mono',text:'Perfil: '+r.profileStateLabel})])]));
return b;}
// ---------- Controles e achados ----------
var listBox,recorte,inputs={};
function controlsTab(){var box=el('div',{id:'tab-controls',role:'tabpanel','aria-labelledby':'t-controls'});box.hidden=true;
box.appendChild(countsPanel());
var f=el('div',{cls:'panel filters',role:'search'});
function sel(name,label,opts){var s=el('select',{id:'f-'+name,onchange:function(){ST.f[name]=s.value;renderList();}},[el('option',{value:'',text:'Todos'})].concat(opts.map(function(o){return el('option',{value:o[0],text:o[1]});})));inputs[name]=s;return el('label',{'for':'f-'+name},[label,s]);}
var q=el('input',{id:'f-q',type:'search',placeholder:'Controle, código, conta, aplicação…',oninput:function(){ST.f.q=q.value;renderList();}});inputs.q=q;
f.appendChild(el('label',{'for':'f-q'},['Pesquisa',q]));
f.appendChild(sel('status','Resultado',[['findings','Achados (reprovado + mitigado)']].concat(STATUS)));
f.appendChild(sel('severity','Severidade',SEV));
f.appendChild(sel('platform','Plataforma',uniq(D.controls.map(function(c){return c.platform||c.provider;})).map(function(x){return[x,x];})));
f.appendChild(sel('service','Serviço',uniq(D.controls.map(function(c){return c.service;})).map(function(x){return[x,x];})));
f.appendChild(sel('domain','Domínio',uniqPairs(D.controls.map(function(c){return[c.domain,c.domainLabel];}))));
f.appendChild(sel('framework','Framework',D.frameworkOptions.map(function(x){return[x,x];})));
f.appendChild(el('button',{cls:'btn',type:'button',onclick:function(){go({});}, text:'Limpar filtros'}));
box.appendChild(f);recorte=el('p',{cls:'recorte',role:'status','aria-live':'polite'});box.appendChild(recorte);listBox=el('div');box.appendChild(listBox);
box.appendChild(sources());if(!document.getElementById('aegis-visuals'))box.appendChild(dist('Resultado por domínio de segurança',D.byDomain,'domain'));return box;}
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
function describeFilter(){var f=ST.f,p=[];if(f.status)p.push('resultado: '+(f.status==='findings'?'achados':lbl(STATUS,f.status)));if(f.severity)p.push('severidade: '+lbl(SEV,f.severity));
if(f.platform)p.push('plataforma: '+f.platform);if(f.service)p.push('serviço: '+f.service);if(f.domain){var d=D.controls.filter(function(c){return c.domain===f.domain;})[0];p.push('domínio: '+(d?d.domainLabel:f.domain));}
if(f.framework)p.push('framework: '+f.framework);if(f.q)p.push('pesquisa: “'+f.q+'”');return p.length?p.join(' · '):'sem filtros (avaliação completa)';}
function renderList(open){var list=D.controls.filter(matches);listBox.textContent='';
recorte.textContent='Mostrando '+list.length+' de '+D.controls.length+' controle(s) · Recorte: '+describeFilter()+' · os filtros afetam só a visualização; o arquivo contém a avaliação completa.';
if(!list.length){listBox.appendChild(el('p',{cls:'empty',text:'Nenhum controle corresponde aos filtros. Use “Limpar filtros”.'}));return;}
list.forEach(function(c){listBox.appendChild(controlItem(c,open===c.id));});}
function controlItem(c,isOpen,pfx){var body=detail(c);body.hidden=!isOpen;var id=(pfx||'')+'ctl-'+c.id;
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
el('span',{cls:'k',text:'Frameworks e benchmarks'}),el('span',{text:fw.length?fw.join(' · '):'—'}),el('span',{cls:'k',text:'Capacidades de coleta usadas'}),el('span',{text:(c.requiredCapabilityLabels||c.requiredCapabilities).join(', ')||'—'}),(c.previewApis&&c.previewApis.length)?el('span',{cls:'k',text:'Leitura em versão preview'}):null,(c.previewApis&&c.previewApis.length)?el('span',{text:c.previewApis.join(' · ')+' — versão não suportada em produção pelo fornecedor; mudança de contrato deixa o controle não avaliado.'}):null,
el('span',{cls:'k',text:'Contribuição para a nota'}),el('span',{text:c.factor===null?'Fora da nota (não avaliado, erro ou não aplicável) — reduz a cobertura, não a nota.':'Peso '+c.weight+' × fator '+String(c.factor).replace('.',',')+' = '+String(c.achieved).replace('.',',')+' de '+c.possible+' ponto(s).'})])]));
var gt=(c.glossaryTerms||[]).filter(function(x){return GL[x];});
if(gt.length)b.appendChild(sec('Termos técnicos deste controle',[termList(gt)]));
return b;}
function termList(terms){var dl=el('dl',{cls:'gl'});terms.forEach(function(x){var g=GL[x];dl.appendChild(el('dt',{text:g.term}));dl.appendChild(el('dd',null,[el('span',{cls:'mean',text:g.meaning+'. '}),g.explanation]));});return dl;}
// ---------- Glossário ----------
function glossaryTab(){var box=el('div',{id:'tab-glossary',role:'tabpanel','aria-labelledby':'t-glossary'});box.hidden=true;
if(D.referenceCoverage)box.appendChild(coverage());box.appendChild(integrity());
var ip=el('div',{cls:'panel'},[el('h2',{text:'Identificadores internos (códigos AK-…)'}),el('p',{cls:'sub',text:D.identifierExplanation||''})]);
var res=el('div',{role:'status','aria-live':'polite'}),lim=20;
var iq=el('input',{id:'g-id',type:'search',placeholder:'Ex.: AK-AZ-IAM-004 ou parte do título',oninput:function(){lim=20;find();},
onkeydown:function(e){if(e.key==='Enter'){var x=CT[iq.value.trim().toUpperCase()];if(x){go({},x.id);e.preventDefault();}}}});
function find(){res.textContent='';var t=iq.value.trim().toLowerCase();if(!t)return;
var m=D.controls.filter(function(c){return c.id.toLowerCase().indexOf(t)>=0||c.title.toLowerCase().indexOf(t)>=0;});
if(!m.length){res.appendChild(el('p',{cls:'empty',text:'Nenhum controle desta avaliação corresponde a “'+iq.value.trim()+'”.'}));return;}
res.appendChild(el('p',{cls:'sub',text:m.length+' controle(s) encontrado(s). Enter com o código completo abre o controle.'}));
m.slice(0,lim).forEach(function(c){res.appendChild(el('div',{cls:'gid'},[ctlRef(c.id,c.title),el('p',{cls:'sub',text:'O que avalia: '+(c.description||'descrição não congelada nesta fotografia; vale o título registrado acima.')})]));});
if(m.length>lim)res.appendChild(el('button',{cls:'linkbtn',type:'button',text:'Mostrar todos os '+m.length+' resultados',onclick:function(){lim=m.length;find();}}));}
ip.appendChild(el('label',{cls:'gfind','for':'g-id'},['Consultar um código ou título',iq]));ip.appendChild(res);box.appendChild(ip);
var g=D.glossary||[],tp=el('div',{cls:'panel'},[el('h2',{text:'Termos técnicos'}),el('p',{cls:'sub',text:'Siglas que aparecem nos textos desta avaliação, com o significado e uma explicação curta. As definições vêm do glossário único do AEGIS — as mesmas da aplicação e do PDF.'})]);
if(!g.length){tp.appendChild(el('p',{cls:'empty',text:'Nenhuma sigla do glossário aparece nos textos desta fotografia.'}));box.appendChild(tp);return box;}
var list=el('div'),tq=el('input',{id:'g-term',type:'search',placeholder:'Sigla ou palavra (ex.: MFA, rede)',oninput:function(){draw();}});
function draw(){list.textContent='';var t=tq.value.trim().toLowerCase();var m=g.filter(function(x){return!t||[x.term,x.meaning,x.explanation].join(' ').toLowerCase().indexOf(t)>=0;});
if(!m.length){list.appendChild(el('p',{cls:'empty',text:'Nenhum termo corresponde à pesquisa.'}));return;}list.appendChild(termList(m.map(function(x){return x.term;})));}
tp.appendChild(el('label',{cls:'gfind','for':'g-term'},['Pesquisar no glossário',tq]));tp.appendChild(list);draw();box.appendChild(tp);return box;}
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
// ---------- [AEGIS-KNIGHT-SCOPE-01] Resultados por fonte (consolidado) ----------
// "Geral" é o consolidado (as abas de seções abaixo). Cada fonte incluída tem a própria aba, desenhada só com o que a fotografia
// congelou: nota, cobertura e contagens da composição e os controles cuja fonte é ela. Nada é lido de fora do arquivo.
var SRC=D.sourceTabs||[],srcNav=null,srcBox=null,srcSel='geral';
function setSrc(k){srcSel=k;if(!srcNav)return;var b=srcNav.querySelectorAll('button');for(var i=0;i<b.length;i++){var on=b[i].getAttribute('data-src')===k;b[i].setAttribute('aria-selected',on?'true':'false');b[i].tabIndex=on?0:-1;}
var geral=k==='geral';tabs.hidden=!geral;srcBox.hidden=geral;if(geral){setTab(ST.tab);return;}
['overview','cis','controls','glossary'].forEach(function(x){document.getElementById('tab-'+x).hidden=true;});renderSrc(k);}
function srcKpi(l,v,n){return el('div',{cls:'kpi'},[el('div',{cls:'l',text:l}),el('div',{cls:'v',text:v}),el('div',{cls:'n',text:n})]);}
function renderSrc(k){var t=SRC.filter(function(x){return x.key===k;})[0];srcBox.textContent='';if(!t)return;
var ctl=D.controls.filter(function(c){return c.source===k;}),fnd=ctl.filter(function(c){return c.status==='Exposed'||c.status==='Mitigated';});
var noScore=t.score===null||t.score===undefined;
srcBox.appendChild(el('h2',{cls:'grp-h',id:'src-h',text:'Resultados — '+t.label}));
srcBox.appendChild(el('p',{cls:'sub src-meta',text:'Dados congelados nesta fotografia. Nota, cobertura e contagens são as da própria fonte, não somadas às demais. Coleta em '+dt(t.capturedAt)+' · catálogo '+(t.catalogVersion||'—')+' · '+(t.sourceStateLabel||'estado da coleta não registrado')+'.'}));
var g=el('div',{cls:'grid g4'});
g.appendChild(srcKpi('Nota da fonte',noScore?'—':String(Math.round(t.score))+'/100',noScore?'Sem nota: nenhum controle avaliado nesta fonte.':'Sobre os '+t.evaluated+' controles avaliados desta fonte.'));
g.appendChild(srcKpi('Cobertura da fonte',pct(t.coverage),t.evaluated+' de '+t.applicable+' controles aplicáveis avaliados'));
g.appendChild(srcKpi('Controles reprovados',String(t.failed),t.mitigated+' mitigado(s) · '+(t.notEvaluated+t.errors)+' sem avaliação'));
g.appendChild(srcKpi('Aprovados',String(t.passed),t.notApplicable+' não aplicável(is) fora da base'));
srcBox.appendChild(g);
var lp=el('div',{cls:'panel'},[el('h2',{text:'Limitações de coleta desta fonte'})]);
if(t.limitations&&t.limitations.length)lp.appendChild(el('ul',null,t.limitations.map(function(x){return el('li',{text:x});})));
else lp.appendChild(el('p',{cls:'sub',text:'Nenhuma limitação de coleta registrada para esta fonte nesta fotografia.'}));
srcBox.appendChild(lp);
var fp=el('div',{cls:'panel'},[el('h2',{text:'Achados e controles desta fonte'}),el('p',{cls:'sub',text:fnd.length+' achado(s) (reprovado ou mitigado) entre '+ctl.length+' controle(s) desta fonte, na ordem do relatório. Clique para abrir o detalhe.'})]);
if(!ctl.length)fp.appendChild(el('p',{cls:'empty',text:'Nenhum controle desta fonte foi congelado na fotografia.'}));
ctl.forEach(function(c){fp.appendChild(controlItem(c,false,'src-'));});
srcBox.appendChild(fp);}
// ---------- Montagem ----------
var tabs=el('div',{cls:'tabs',role:'tablist','aria-label':'Seções do relatório'});
var TABS=[['overview','Resumo e resultados'],['cis','CIS Benchmarks'],['controls','Detalhes técnicos'],['glossary','Metodologia e glossário']];
TABS.forEach(function(t,i){tabs.appendChild(el('button',{id:'t-'+t[0],type:'button',role:'tab','data-tab':t[0],'aria-controls':'tab-'+t[0],'aria-selected':i===0?'true':'false',text:t[1],
onclick:function(){setTab(t[0]);},onkeydown:function(e){if(e.key==='ArrowRight'||e.key==='ArrowLeft'){var n=TABS[(i+(e.key==='ArrowRight'?1:TABS.length-1))%TABS.length][0];setTab(n);document.getElementById('t-'+n).focus();e.preventDefault();}}}));});
if(SRC.length){srcNav=el('div',{cls:'tabs srcnav',role:'tablist','aria-label':'Resultados: geral e por fonte'});
var SK=[['geral','Geral']].concat(SRC.map(function(t){return[t.key,t.label];}));
SK.forEach(function(t,i){srcNav.appendChild(el('button',{id:'s-'+t[0],type:'button',role:'tab','data-src':t[0],'aria-controls':i===0?'tab-overview':'src-view','aria-selected':i===0?'true':'false',tabindex:i===0?'0':'-1',text:t[1],
onclick:function(){setSrc(t[0]);},onkeydown:function(e){var n=null;if(e.key==='ArrowRight')n=SK[(i+1)%SK.length][0];else if(e.key==='ArrowLeft')n=SK[(i+SK.length-1)%SK.length][0];else if(e.key==='Home')n=SK[0][0];else if(e.key==='End')n=SK[SK.length-1][0];
if(n!==null){setSrc(n);document.getElementById('s-'+n).focus();e.preventDefault();}}}));});
srcBox=el('section',{id:'src-view',role:'tabpanel','aria-labelledby':'src-h',cls:'grp'});srcBox.hidden=true;main.appendChild(srcNav);}
main.appendChild(tabs);main.appendChild(overview());main.appendChild(cisTab());main.appendChild(controlsTab());main.appendChild(glossaryTab());if(srcBox)main.appendChild(srcBox);renderList();setTab('overview');
// [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Os riscos prioritários (desenhados no servidor) abrem o detalhe do controle.
Array.prototype.forEach.call(document.querySelectorAll('[data-open]'),function(a){a.addEventListener('click',function(e){e.preventDefault();go({},a.getAttribute('data-open'));});});
// [AEGIS-ASSESSMENT-VISUALS-01] Links dos gráficos e das tabelas de valores abrem a aba de controles já filtrada.
Array.prototype.forEach.call(document.querySelectorAll('[data-filter]'),function(a){a.addEventListener('click',function(e){e.preventDefault();var o={};
a.getAttribute('data-filter').split('&').forEach(function(kv){var i=kv.indexOf('=');if(i>0)o[kv.slice(0,i)]=decodeURIComponent(kv.slice(i+1));});go(o);});});
var ns=document.getElementById('nojs');if(ns)ns.hidden=true;
})();
""";
}
