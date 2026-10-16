import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { fmtCompact } from '../format'
import { useNow } from '../hooks'
import { useLive, useMeter, type MeterSample } from '../liveContext'

const WINDOWS = [
  { key: '1m', ms: 60_000 },
  { key: '5m', ms: 300_000 },
  { key: '10m', ms: 600_000 },
] as const
type WindowKey = (typeof WINDOWS)[number]['key']

const STORAGE_KEY = 'aigw.throughputWindow'
/** A second's sample arrives just after it ends; drawing one second behind lets each bar slide in from the right edge. */
const DELAY_MS = 1000
const OUT_H = 110
const PROMPT_H = 48

function loadWindow(): WindowKey {
  try {
    const v = localStorage.getItem(STORAGE_KEY)
    if (v === '1m' || v === '5m' || v === '10m') return v
  } catch {
    /* ignore */
  }
  return '1m'
}

/** Step path over [from, to): one flat segment per second, missing seconds at 0. `area` closes it to the baseline. */
function stepPath(samples: Map<number, number>, from: number, to: number, x: (t: number) => number, y: (v: number) => number, area: boolean) {
  const base = y(0)
  let d = `M${x(from).toFixed(1)},${base.toFixed(1)}`
  for (let t = from; t < to; t += 1000) {
    const yy = y(samples.get(t) ?? 0).toFixed(1)
    d += `L${x(t).toFixed(1)},${yy}L${x(t + 1000).toFixed(1)},${yy}`
  }
  d += `L${x(to).toFixed(1)},${base.toFixed(1)}`
  return area ? d + 'Z' : d
}

/** Rounded-up axis maximum (1, 2, 2.5, 5 × 10^n). */
function niceMax(v: number) {
  if (v <= 0) return 1
  const p = 10 ** Math.floor(Math.log10(v))
  return ([1, 2, 2.5, 5, 10].find((m) => m * p >= v) ?? 10) * p
}

function Strip({
  label,
  series,
  height,
  width,
  className,
  build,
  groupRef,
}: {
  label: string
  series: Map<number, number>
  height: number
  width: number
  className: string
  build: { from: number; to: number; base: number; k: number; max: number }
  groupRef: (g: SVGGElement | null) => void
}) {
  const top = 4
  const y = (v: number) => height - 1 - (v / build.max) * (height - top - 1)
  const x = (t: number) => (t - build.base) * build.k
  const clip = `clip-${className}`
  return (
    <svg className={`tp-strip ${className}`} width={width} height={height} role="img" aria-label={label}>
      <defs>
        <clipPath id={clip}>
          <rect x={0} y={0} width={width} height={height} />
        </clipPath>
      </defs>
      {[0.5, 1].map((f) => (
        <line key={f} className="tp-grid" x1={0} x2={width} y1={y(build.max * f)} y2={y(build.max * f)} />
      ))}
      <line className="tp-axis" x1={0} x2={width} y1={height - 0.5} y2={height - 0.5} />
      <g clipPath={`url(#${clip})`}>
        <g ref={groupRef}>
          <path className="tp-area" d={stepPath(series, build.from, build.to, x, y, true)} />
          <path className="tp-line" d={stepPath(series, build.from, build.to, x, y, false)} />
        </g>
      </g>
      <text className="tp-max" x={4} y={12}>
        {fmtCompact(build.max)}
      </text>
    </svg>
  )
}

/** Gateway-wide token throughput, one-second resolution, scrolling continuously like a network traffic meter. */
export default function LiveThroughput() {
  const { status } = useLive()
  const { samples, inFlight, skew } = useMeter()
  // Re-renders once a second (samples arrive at the same rate); motion between renders is the frame loop below.
  const now = useNow(1000) - skew
  const [win, setWin] = useState<WindowKey>(loadWindow)
  const winMs = WINDOWS.find((w) => w.key === win)!.ms

  const boxRef = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    const el = boxRef.current
    if (!el) return
    const ro = new ResizeObserver(() => setWidth(Math.floor(el.clientWidth)))
    ro.observe(el)
    setWidth(Math.floor(el.clientWidth))
    return () => ro.disconnect()
  }, [])

  const newest = samples.length ? samples[samples.length - 1].t : 0

  const outputs = useMemo(() => new Map(samples.map((s: MeterSample) => [s.t, s.output])), [samples])
  const prompts = useMemo(() => new Map(samples.map((s: MeterSample) => [s.t, s.prompt])), [samples])

  // Geometry is rebuilt once per sample; between samples the paths are only translated (no React render per frame).
  const build = useMemo(() => {
    const viewStart = now - DELAY_MS - winMs
    const from = Math.floor((viewStart - 2000) / 1000) * 1000
    const to = Math.max(from, (newest || Math.floor(now / 1000) * 1000 - 1000) + 1000)
    const visible = samples.filter((s) => s.t + 1000 >= viewStart - 1000)
    return {
      builtAt: now,
      from,
      to,
      base: viewStart,
      k: width > 0 ? width / winMs : 0,
      outMax: niceMax(Math.max(0, ...visible.map((s) => s.output)) * 1.1),
      promptMax: niceMax(Math.max(0, ...visible.map((s) => s.prompt)) * 1.1),
    }
  }, [samples, newest, width, winMs, now])

  const groups = useRef<(SVGGElement | null)[]>([])
  useEffect(() => {
    const apply = () => {
      const tx = -(Date.now() - skew - build.builtAt) * build.k
      for (const g of groups.current) g?.setAttribute('transform', `translate(${tx.toFixed(2)},0)`)
    }
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
      apply()
      return
    }
    let raf = 0
    const frame = () => {
      apply()
      raf = requestAnimationFrame(frame)
    }
    raf = requestAnimationFrame(frame)
    return () => cancelAnimationFrame(raf)
  }, [build, skew])

  const last = samples.length ? samples[samples.length - 1] : undefined
  const fresh = last !== undefined && now - (last.t + 1000) < 3000
  const out = fresh ? last.output : 0
  const prompt = fresh ? last.prompt : 0

  const setWindow = (k: WindowKey) => {
    setWin(k)
    try {
      localStorage.setItem(STORAGE_KEY, k)
    } catch {
      /* ignore */
    }
  }

  return (
    <section className="panel throughput" aria-labelledby="tp-title">
      <header className="panel-head tp-head">
        <h3 id="tp-title">Live throughput</h3>
        <div className="tp-values" aria-live="off">
          <span className="tp-value out" title="Output tokens generated in the last second, all accounts">
            <span aria-hidden="true">↓</span> {fmtCompact(out)} <small>tok/s</small>
          </span>
          <span className="tp-value prompt" title="Prompt tokens sent (incl. cache) in the last second">
            <span aria-hidden="true">↑</span> {fmtCompact(prompt)} <small>tok/s</small>
          </span>
          <span className="tp-inflight muted">{inFlight} in flight</span>
        </div>
        <span className="spacer" />
        <div className="seg" role="group" aria-label="Throughput window">
          {WINDOWS.map((w) => (
            <button key={w.key} className={win === w.key ? 'on' : ''} onClick={() => setWindow(w.key)}>
              {w.key}
            </button>
          ))}
        </div>
      </header>
      <div ref={boxRef} className="tp-chart">
        {width > 0 && (
          <>
            <div className="tp-label">Generating</div>
            <Strip
              label="Output tokens per second"
              series={outputs}
              height={OUT_H}
              width={width}
              className="out"
              build={{ ...build, max: build.outMax }}
              groupRef={(g) => (groups.current[0] = g)}
            />
            <div className="tp-label">Prompt</div>
            <Strip
              label="Prompt tokens per second"
              series={prompts}
              height={PROMPT_H}
              width={width}
              className="prompt"
              build={{ ...build, max: build.promptMax }}
              groupRef={(g) => (groups.current[1] = g)}
            />
            <div className="tp-axis-labels muted">
              <span>−{win}</span>
              <span>{status === 'live' ? 'now' : 'paused (no live connection)'}</span>
            </div>
          </>
        )}
      </div>
    </section>
  )
}
