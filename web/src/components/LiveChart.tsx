import { useId, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useNow } from '../hooks'
import { useMeter } from '../liveContext'
import { LIVE_WINDOWS, type LiveSeries } from '../liveWindows'

/** Data for a second is complete just after it ends; drawing one second behind lets bars slide in from the right edge. */
const DELAY_MS = 1000
/** A per-request series (an agent) is drawn until this long after its last request, the gateway's "active" gap. */
const IDLE_MS = 60_000

function niceMax(v: number) {
  if (v <= 0) return 1
  const p = 10 ** Math.floor(Math.log10(v))
  return ([1, 2, 2.5, 5, 10].find((m) => m * p >= v) ?? 10) * p
}

const f1 = (n: number) => n.toFixed(1)

/**
 * A scrolling real-time chart (like a network traffic meter): geometry is rebuilt when data arrives (about once a
 * second) and only translated in between, every animation frame.
 * - `bucketMs` set: per-bucket values; `stack` draws stacked step areas (missing buckets are 0), `area` overlapping step areas
 *   from zero (largest behind, so small ones stay visible at the bottom), `line` step lines with gaps.
 * - `bucketMs` null: per-request values (e.g. one series per agent), each held until the series' next point.
 */
export default function LiveChart({
  series,
  bucketMs,
  mode,
  windowMs,
  format,
  axisFormat = format,
  height = 300,
  emptyText = 'Waiting for traffic…',
}: {
  series: LiveSeries[]
  bucketMs: number | null
  mode: 'stack' | 'area' | 'line'
  windowMs: number
  format: (v: number) => string
  axisFormat?: (v: number) => string
  height?: number
  emptyText?: string
}) {
  const { skew } = useMeter()
  const now = useNow(1000) - skew
  const [hidden, setHidden] = useState<Set<string>>(new Set())
  const visible = useMemo(() => series.filter((s) => !hidden.has(s.key)), [series, hidden])
  const clipId = `live-clip-${useId().replace(/:/g, '')}`

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

  const plotH = height - 6
  const build = useMemo(() => {
    const viewStart = now - DELAY_MS - windowMs
    const k = width > 0 ? width / windowMs : 0
    const x = (t: number) => (t - viewStart) * k
    const from = viewStart - 2 * (bucketMs ?? 1000)
    const paths: Array<{ key: string; color: string; area?: string; line: string }> = []
    let max = 0

    if (bucketMs !== null && (mode === 'stack' || mode === 'area')) {
      // Step bands over a common bucket grid: stacked on each other, or each from zero (largest drawn first, behind).
      const start = Math.floor(from / bucketMs) * bucketMs
      const last = Math.max(start, ...visible.map((s) => (s.points.length ? s.points[s.points.length - 1][0] : start)))
      const grid: number[] = []
      for (let t = start; t <= last; t += bucketMs) grid.push(t)
      const lower = grid.map(() => 0)
      const overlay = mode === 'area'
      const ordered = overlay
        ? [...visible].sort((a, b) => Math.max(0, ...b.points.map((p) => p[1])) - Math.max(0, ...a.points.map((p) => p[1])))
        : visible
      const bands = ordered.map((s) => {
        const byT = new Map(s.points)
        const upper = grid.map((t, i) => lower[i] + (byT.get(t) ?? 0))
        const band = { s, lo: [...lower], hi: upper }
        if (!overlay) upper.forEach((v, i) => (lower[i] = v))
        grid.forEach((t, i) => {
          if (t + bucketMs >= viewStart) max = Math.max(max, upper[i])
        })
        return band
      })
      const top = niceMax(max * 1.1)
      const y = (v: number) => plotH - (v / top) * (plotH - 4)
      for (const b of bands) {
        let up = ''
        let down = ''
        // Outline only the seconds where this band has a value, so empty bands don't trace over the stack's top.
        let line = ''
        let drawing = false
        grid.forEach((t, i) => {
          up += `${i === 0 ? 'M' : 'L'}${f1(x(t))},${f1(y(b.hi[i]))}L${f1(x(t + bucketMs))},${f1(y(b.hi[i]))}`
          if (b.hi[i] > b.lo[i]) {
            line += `${drawing ? 'L' : 'M'}${f1(x(t))},${f1(y(b.hi[i]))}L${f1(x(t + bucketMs))},${f1(y(b.hi[i]))}`
            drawing = true
          } else drawing = false
        })
        for (let i = grid.length - 1; i >= 0; i--) {
          down += `L${f1(x(grid[i] + bucketMs))},${f1(y(b.lo[i]))}L${f1(x(grid[i]))},${f1(y(b.lo[i]))}`
        }
        paths.push({ key: b.s.key, color: b.s.color, area: up + down + 'Z', line })
      }
      return { builtAt: now, k, top, paths }
    }

    // Lines: per bucket (gaps where a bucket has no value) or per request (held until the next point).
    for (const s of visible) {
      let d = ''
      let prevEnd: number | null = null
      s.points.forEach(([t, v], i) => {
        const next = s.points[i + 1]
        const end = bucketMs !== null ? t + bucketMs : next ? next[0] : Math.max(t + 1000, Math.min(now - DELAY_MS, t + IDLE_MS))
        if (end < from) return
        if (end >= viewStart) max = Math.max(max, v)
        const continuous = prevEnd !== null && Math.abs(prevEnd - t) < 1
        d += `${continuous ? 'L' : 'M'}${f1(x(t))},§${v}§L${f1(x(end))},§${v}§`
        prevEnd = end
      })
      paths.push({ key: s.key, color: s.color, line: d })
    }
    const top = niceMax(max * 1.1)
    const y = (v: number) => plotH - (v / top) * (plotH - 4)
    for (const p of paths) p.line = p.line.replace(/§([-\d.e+]+)§/g, (_, v: string) => f1(y(Number(v))))
    return { builtAt: now, k, top, paths }
  }, [visible, bucketMs, mode, windowMs, width, now, plotH])

  // Hover readout: the time under the cursor moves as the chart scrolls, so it is recomputed every frame and the readout
  // re-renders only when it lands in another bucket (or, per request, every quarter second).
  const hoverX = useRef<number | null>(null)
  const [hover, setHover] = useState<{ x: number; t: number } | null>(null)
  const hoverKey = useRef<number | null>(null)

  // Offset applied before paint, then every frame (see LiveThroughput for why it must not wait for a normal effect).
  const groupRef = useRef<SVGGElement>(null)
  useLayoutEffect(() => {
    const apply = () => {
      const serverNow = Date.now() - skew
      groupRef.current?.setAttribute('transform', `translate(${(-(serverNow - build.builtAt) * build.k).toFixed(2)},0)`)
      const x = hoverX.current
      if (x === null || build.k === 0) return
      const t = serverNow - DELAY_MS - windowMs + x / build.k
      const key = bucketMs !== null ? Math.floor(t / bucketMs) : Math.floor(t / 250)
      if (key !== hoverKey.current) {
        hoverKey.current = key
        setHover({ x, t })
      }
    }
    apply()
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
    let raf = 0
    const frame = () => {
      apply()
      raf = requestAnimationFrame(frame)
    }
    raf = requestAnimationFrame(frame)
    return () => cancelAnimationFrame(raf)
  }, [build, skew, bucketMs, windowMs])

  const onMove = (e: React.MouseEvent<SVGSVGElement>) => {
    const x = e.clientX - e.currentTarget.getBoundingClientRect().left
    hoverX.current = x
    hoverKey.current = null
    // The next frame computes the time; until then keep the readout where the cursor is.
    setHover((h) => (h ? { ...h, x } : h))
  }
  const onLeave = () => {
    hoverX.current = null
    hoverKey.current = null
    setHover(null)
  }

  /** Value of a series at time t: its bucket (bucketed) or its latest request still held (per request). */
  const valueAt = (s: LiveSeries, t: number): number | null => {
    if (bucketMs !== null) {
      const b = Math.floor(t / bucketMs) * bucketMs
      const p = s.points.find(([pt]) => pt === b)
      return p ? p[1] : mode === 'line' ? null : 0
    }
    let held: number | null = null
    for (let i = 0; i < s.points.length; i++) {
      const [pt, v] = s.points[i]
      if (pt > t) break
      const end = i + 1 < s.points.length ? s.points[i + 1][0] : pt + IDLE_MS
      held = t < end ? v : null
    }
    return held
  }
  const readout = hover
    ? visible
        .map((s) => ({ s, v: valueAt(s, hover.t) }))
        .filter((r) => r.v !== null && (mode === 'line' || r.v !== 0))
        .sort((a, b) => (b.v ?? 0) - (a.v ?? 0))
    : []
  const readoutTotal = mode === 'stack' ? readout.reduce((sum, r) => sum + (r.v ?? 0), 0) : null

  // Legend values: the latest bucket (bucketed) or each series' latest request (per request).
  const latest = (s: LiveSeries) => {
    const last = s.points[s.points.length - 1]
    if (!last) return null
    if (bucketMs !== null && last[0] + bucketMs < now - DELAY_MS - 2 * bucketMs) return mode === 'line' ? null : 0
    return last[1]
  }
  const toggle = (key: string) =>
    setHidden((h) => {
      const n = new Set(h)
      if (n.has(key)) n.delete(key)
      else n.add(key)
      return n
    })

  const empty = series.every((s) => s.points.length === 0)
  const y = (v: number) => plotH - (v / build.top) * (plotH - 4)
  return (
    <div className="live-chart">
      <div ref={boxRef} className="live-chart-plot" style={{ height }}>
        {width > 0 && (
          <svg width={width} height={height} role="img" aria-label="Live chart" onMouseMove={onMove} onMouseLeave={onLeave}>
            <defs>
              <clipPath id={clipId}>
                <rect x={0} y={0} width={width} height={height} />
              </clipPath>
            </defs>
            {[0.5, 1].map((fr) => (
              <line key={fr} className="tp-grid" x1={0} x2={width} y1={y(build.top * fr)} y2={y(build.top * fr)} />
            ))}
            <line className="tp-axis" x1={0} x2={width} y1={plotH + 0.5} y2={plotH + 0.5} />
            <g clipPath={`url(#${clipId})`}>
              <g ref={groupRef}>
                {build.paths.map((p) => (
                  <g key={p.key}>
                    {p.area && <path d={p.area} fill={p.color} fillOpacity={mode === 'area' ? 0.22 : 0.18} />}
                    <path d={p.line} fill="none" stroke={p.color} strokeWidth={1.5} strokeLinejoin="round" />
                  </g>
                ))}
              </g>
            </g>
            <text className="tp-max" x={4} y={12}>
              {axisFormat(build.top)}
            </text>
            {hover && <line className="live-cursor" x1={hover.x} x2={hover.x} y1={0} y2={plotH} />}
            {empty && (
              <text className="live-chart-empty" x={width / 2} y={height / 2} textAnchor="middle">
                {emptyText}
              </text>
            )}
          </svg>
        )}
        {hover && (
          <div
            className="live-readout"
            style={hover.x > width - 240 ? { right: width - hover.x + 12 } : { left: hover.x + 12 }}
            role="status"
          >
            <div className="when">{new Date(hover.t + skew).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</div>
            {readout.length === 0 ? (
              <div className="muted">No activity</div>
            ) : (
              <>
                {readout.slice(0, 12).map(({ s, v }) => (
                  <div key={s.key} className="row">
                    <span className="swatch" style={{ background: s.color }} aria-hidden="true" />
                    <span className="name">{s.label}</span>
                    <span className="value">{format(v ?? 0)}</span>
                  </div>
                ))}
                {readout.length > 12 && <div className="muted">+{readout.length - 12} more</div>}
                {readoutTotal !== null && readout.length > 1 && (
                  <div className="row total">
                    <span className="name">Total</span>
                    <span className="value">{format(readoutTotal)}</span>
                  </div>
                )}
              </>
            )}
          </div>
        )}
      </div>
      <div className="tp-axis-labels muted">
        <span>−{LIVE_WINDOWS.find((w) => w.ms === windowMs)?.key ?? `${windowMs / 1000}s`}</span>
        <span>now</span>
      </div>
      {series.length > 0 && (
        <ul className="live-legend">
          {series.map((s) => {
            const v = latest(s)
            return (
              <li key={s.key}>
                <button className={hidden.has(s.key) ? 'off' : ''} onClick={() => toggle(s.key)} aria-pressed={!hidden.has(s.key)}>
                  <span className="swatch" style={{ background: s.color }} aria-hidden="true" />
                  <span className="name">{s.label}</span>
                  <span className="value">{v === null ? '—' : format(v)}</span>
                </button>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
