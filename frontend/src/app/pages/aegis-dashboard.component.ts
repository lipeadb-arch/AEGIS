import { Component, ElementRef, OnInit, effect, inject, signal, viewChild } from '@angular/core';
import { AegisScoreService } from '../services/aegis-score.service';
import { TenantTrendDto } from '../models/aegis-score.models';
import { environment } from '../../environments/environment';

/**
 * HUD Tático do Aegis Score — a tendência de postura de segurança do tenant no modelo de um painel
 * de SOC. Consome GET /api/v1/aegis-score/trend e destaca o Score Atual (o dia mais recente).
 */
@Component({
  selector: 'app-aegis-dashboard',
  standalone: true,
  templateUrl: './aegis-dashboard.component.html',
})
export class AegisDashboardComponent implements OnInit {
  private readonly svc = inject(AegisScoreService);

  /** Série temporal crua (últimos 30 dias, ordem cronológica crescente). */
  readonly trend = signal<TenantTrendDto[]>([]);
  readonly loading = signal(true);
  readonly loadError = signal(false);

  /** KPI "Controles Pendentes" — null enquanto carrega (indicador discreto no HUD). */
  readonly pendingControls = signal<number | null>(null);

  /**
   * KPI hero — Score Atual (%). Vem do endpoint /current (tempo real sobre o TenantControlState), NÃO
   * da série diária: reflete avaliações recém-processadas (ex.: Govern) sem esperar a foto da meia-noite.
   */
  readonly currentScore = signal(0);

  /** Exposto ao template para orientar o diagnóstico quando a carga falha. */
  protected readonly apiBase = environment.apiBase;

  /** Canvas do gráfico — só existe no DOM quando há série (branch @else do template). */
  private readonly canvasRef = viewChild<ElementRef<HTMLCanvasElement>>('chartCanvas');

  constructor() {
    // Redesenho reativo: dispara quando a série OU o canvas mudam. Canvas nativo (sem instância de
    // lib a acumular): cada passada limpa e repinta o 2D — atualização destrutiva, sem leak. O
    // ResizeObserver mantém o gráfico nítido no resize e é desconectado a cada re-run e no destroy.
    effect((onCleanup) => {
      const canvas = this.canvasRef()?.nativeElement;
      const data = this.trend();
      if (!canvas || data.length === 0) return;

      this.drawTrend(canvas, data);

      const ro = new ResizeObserver(() => this.drawTrend(canvas, data));
      ro.observe(canvas);
      onCleanup(() => ro.disconnect());
    });
  }

  ngOnInit(): void {
    // Duas chamadas independentes e paralelas: cada KPI reflete o próprio estado de carga/erro.
    this.svc.fetchTrend(30).subscribe({
      next: (series) => {
        this.trend.set(series);
        this.loading.set(false);
      },
      error: (err) => {
        console.error('Falha ao carregar a tendência do Aegis Score:', err);
        this.loadError.set(true);
        this.loading.set(false);
      },
    });

    this.svc.fetchPendingControls().subscribe({
      next: (count) => this.pendingControls.set(count),
      error: (err) => console.error('Falha ao carregar os controles pendentes:', err),
    });

    // Score Atual (KPI hero): tempo real, independente da série diária — destrava o 0.0% do HUD assim
    // que o Govern processa avaliações e grava os TenantControlState.
    this.svc.fetchCurrentScore().subscribe({
      next: (score) => this.currentScore.set(score.percentage),
      error: (err) => console.error('Falha ao carregar o Score Atual do Aegis Score:', err),
    });
  }

  /**
   * Desenha a tendência no <canvas> com a Canvas 2D API (sem libs, como os demais gráficos do
   * projeto). Aura SOC: linha esmeralda-neon com glow, preenchimento em gradiente translúcido e
   * nós discretos. Eixo Y travado em 0–100 (escala de %); eixo X com datas dd/MM esparsas.
   */
  private drawTrend(canvas: HTMLCanvasElement, data: TenantTrendDto[]): void {
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    // Buffer em devicePixelRatio para nitidez; desenho em coordenadas CSS.
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth || 600;
    const h = canvas.clientHeight || 340;
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h); // destrutivo: zera antes de repintar

    const padL = 32, padR = 14, padT = 16, padB = 26;
    const plotW = w - padL - padR;
    const plotH = h - padT - padB;
    const emerald = '#00e5a0';

    // Eixo Y travado 0..100 (escala de porcentagem).
    const yFor = (pct: number) => padT + plotH * (1 - Math.min(100, Math.max(0, pct)) / 100);
    const xFor = (i: number) =>
      padL + (data.length === 1 ? plotW / 2 : (plotW * i) / (data.length - 1));

    // Grade horizontal + rótulos do eixo Y.
    ctx.font = '10px "JetBrains Mono", ui-monospace, monospace';
    ctx.textBaseline = 'middle';
    ctx.textAlign = 'right';
    for (const g of [0, 25, 50, 75, 100]) {
      const y = yFor(g);
      ctx.strokeStyle = 'rgba(122,145,190,0.12)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(padL, y);
      ctx.lineTo(w - padR, y);
      ctx.stroke();
      ctx.fillStyle = 'rgba(135,145,168,0.85)';
      ctx.fillText(String(g), padL - 6, y);
    }

    // Rótulos do eixo X (dd/MM), esparsos para não poluir.
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    const step = Math.max(1, Math.ceil(data.length / 6));
    data.forEach((pt, i) => {
      if (i % step !== 0 && i !== data.length - 1) return;
      ctx.fillStyle = 'rgba(135,145,168,0.85)';
      ctx.fillText(formatDdMm(pt.snapshotDate), xFor(i), h - padB + 7);
    });

    // Preenchimento em gradiente translúcido sob a linha.
    const grad = ctx.createLinearGradient(0, padT, 0, padT + plotH);
    grad.addColorStop(0, 'rgba(0,229,160,0.30)');
    grad.addColorStop(1, 'rgba(0,229,160,0.02)');
    ctx.beginPath();
    data.forEach((pt, i) => {
      const x = xFor(i), y = yFor(pt.percentage);
      i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
    });
    ctx.lineTo(xFor(data.length - 1), padT + plotH);
    ctx.lineTo(xFor(0), padT + plotH);
    ctx.closePath();
    ctx.fillStyle = grad;
    ctx.fill();

    // Linha neon com glow.
    ctx.beginPath();
    data.forEach((pt, i) => {
      const x = xFor(i), y = yFor(pt.percentage);
      i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
    });
    ctx.strokeStyle = emerald;
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    ctx.shadowColor = 'rgba(0,229,160,0.6)';
    ctx.shadowBlur = 10;
    ctx.stroke();
    ctx.shadowBlur = 0; // reseta para não borrar os nós

    // Nós discretos nos pontos de dados.
    for (let i = 0; i < data.length; i++) {
      const x = xFor(i), y = yFor(data[i].percentage);
      ctx.beginPath();
      ctx.arc(x, y, 2.6, 0, Math.PI * 2);
      ctx.fillStyle = emerald;
      ctx.fill();
      ctx.lineWidth = 1;
      ctx.strokeStyle = 'rgba(5,7,15,0.9)';
      ctx.stroke();
    }
  }
}

/** "2026-07-09" → "09/07" (rótulo limpo do eixo X). */
function formatDdMm(iso: string): string {
  const parts = iso.split('-');
  return parts.length === 3 ? `${parts[2]}/${parts[1]}` : iso;
}
