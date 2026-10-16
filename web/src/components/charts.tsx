import { useState } from 'react'
import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import type { AgentSeriesResult, BreakdownRow, Timeseries } from '../types'
import { fmtAxisTime, fmtCompact, fmtDateTime, fmtInt } from '../format'
import { PALETTE } from '../metrics'

const axisStyle = { fontSize: 11, fill: 'var(--muted)' }

export function TimeChart({
  data,
  format,
  mode,
  height = 260,
  labels,
  axisFormat = format,
}: {
  data: Timeseries
  format: (v: number) => string
  /** stack = stacked areas (sums), line = independent lines (averages, peaks) */
  mode: 'stack' | 'line'
  height?: number
  /** Overrides series labels (e.g. metric names when several metrics are plotted). */
  labels?: Record<string, string>
  /** Shorter formatter for axis ticks (defaults to `format`). */
  axisFormat?: (v: number) => string
}) {
  if (data.points.length === 0 || data.series.length === 0)
    return <div className="state muted" style={{ height }}>No data in this range.</div>

  const series = data.series.map((s, i) => ({
    ...s,
    name: labels?.[s.key] ?? s.label,
    color: s.label.startsWith('Other') ? PALETTE[8] : PALETTE[i % 8],
    // Sparse lines (e.g. an average with data in a few buckets) need dots to be visible at all.
    sparse: data.points.filter((p) => p[s.key] !== undefined).length <= 40,
  }))
  const common = {
    data: data.points,
    margin: { top: 8, right: 12, bottom: 0, left: 0 },
  }
  const xAxis = (
    <XAxis
      dataKey="t"
      type="number"
      scale="time"
      domain={['dataMin', 'dataMax']}
      tickFormatter={(v: number) => fmtAxisTime(v, data.bucketMs)}
      tick={axisStyle}
      stroke="var(--border)"
      minTickGap={40}
    />
  )
  const yAxis = <YAxis tickFormatter={(v: number) => axisFormat(v)} tick={axisStyle} stroke="var(--border)" width={64} />
  const grid = <CartesianGrid stroke="var(--grid)" vertical={false} />
  const tooltip = (
    <Tooltip
      contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 12 }}
      labelFormatter={(v) => fmtDateTime(Number(v))}
      formatter={(v) => (typeof v === 'number' ? format(v) : String(v ?? '—'))}
    />
  )
  const legend = series.length > 1 ? <Legend wrapperStyle={{ fontSize: 12 }} /> : null

  return (
    <div className="chart" style={{ height }}>
      <ResponsiveContainer width="100%" height="100%">
        {mode === 'line' ? (
          <LineChart {...common}>
            {grid}
            {xAxis}
            {yAxis}
            {tooltip}
            {legend}
            {series.map((s) => (
              <Line
                key={s.key}
                dataKey={s.key}
                name={s.name}
                stroke={s.color}
                dot={s.sparse ? { r: 2.5 } : false}
                strokeWidth={2}
                connectNulls
                isAnimationActive={false}
              />
            ))}
          </LineChart>
        ) : (
          <AreaChart {...common}>
            {grid}
            {xAxis}
            {yAxis}
            {tooltip}
            {legend}
            {series.map((s) => (
              <Area
                key={s.key}
                dataKey={s.key}
                name={s.name}
                stroke={s.color}
                fill={s.color}
                fillOpacity={0.25}
                stackId="a"
                type="monotone"
                isAnimationActive={false}
              />
            ))}
          </AreaChart>
        )}
      </ResponsiveContainer>
    </div>
  )
}

const tooltipStyle = { background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 12 }

/** Legend entries that toggle their series; hidden ones are dimmed. */
function useHidden() {
  const [hidden, setHidden] = useState<ReadonlySet<string>>(new Set())
  const toggle = (key: unknown) =>
    setHidden((h) => {
      const next = new Set(h)
      const k = String(key)
      if (next.has(k)) next.delete(k)
      else next.add(k)
      return next
    })
  return { hidden, toggle }
}

/**
 * One line per agent instance with a point per request, so each line starts where the agent first appeared (e.g. how a
 * subagent's context grew while the main agent waited). Click legend entries to hide agents.
 */
export function AgentChart({
  data,
  format,
  height = 260,
  sessionScoped = false,
  axisFormat = format,
}: {
  data: AgentSeriesResult
  format: (v: number) => string
  axisFormat?: (v: number) => string
  height?: number
  /** Drop the session prefix from labels (the chart shows one session). */
  sessionScoped?: boolean
}) {
  const { hidden, toggle } = useHidden()
  if (data.agents.length === 0) return <div className="state muted" style={{ height }}>No agent activity in this range.</div>

  // One row per request time; each agent fills only its own key, and lines connect across the gaps.
  const byTime = new Map<number, { t: number } & Record<string, number>>()
  for (const a of data.agents)
    for (const [t, v] of a.points) {
      const row = byTime.get(t) ?? ({ t } as { t: number } & Record<string, number>)
      row[a.key] = v
      byTime.set(t, row)
    }
  const points = [...byTime.values()].sort((x, y) => x.t - y.t)
  const span = points.length > 1 ? points[points.length - 1].t - points[0].t : 60_000
  const label = (l: string) => (sessionScoped ? l.split(' · ').slice(1).join(' · ') || l : l)

  return (
    <>
      <div className="chart" style={{ height }}>
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={points} margin={{ top: 8, right: 12, bottom: 0, left: 0 }}>
            <CartesianGrid stroke="var(--grid)" vertical={false} />
            <XAxis
              dataKey="t"
              type="number"
              scale="time"
              domain={['dataMin', 'dataMax']}
              tickFormatter={(v: number) =>
                span < 10 * 60_000
                  ? new Date(v).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
                  : fmtAxisTime(v, span / 100)
              }
              tick={axisStyle}
              stroke="var(--border)"
              minTickGap={40}
            />
            <YAxis tickFormatter={(v: number) => axisFormat(v)} tick={axisStyle} stroke="var(--border)" width={64} />
            <Tooltip
              contentStyle={tooltipStyle}
              labelFormatter={(v) => fmtDateTime(Number(v))}
              formatter={(v) => (typeof v === 'number' ? format(v) : String(v ?? '—'))}
            />
            <Legend
              wrapperStyle={{ fontSize: 12, cursor: 'pointer' }}
              onClick={(e) => toggle(e.dataKey)}
              formatter={(value, entry) => (
                <span style={{ opacity: hidden.has(String(entry.dataKey)) ? 0.4 : 1 }}>{value}</span>
              )}
            />
            {data.agents.map((a, i) => (
              <Line
                key={a.key}
                dataKey={a.key}
                name={label(a.label)}
                stroke={PALETTE[i % 8]}
                strokeWidth={a.role === 'main' ? 2.5 : 1.5}
                strokeDasharray={a.role === 'utility' ? '4 3' : undefined}
                type={data.cumulative ? 'stepAfter' : 'linear'}
                dot={{ r: 2.5 }}
                connectNulls
                hide={hidden.has(a.key)}
                isAnimationActive={false}
              />
            ))}
          </LineChart>
        </ResponsiveContainer>
      </div>
      {data.hidden > 0 && (
        <p className="muted small chart-note">
          Showing the {data.agents.length} busiest of {data.totalAgents} agents. Filter by session or agent type to see the
          others.
        </p>
      )}
    </>
  )
}

const MODE_COLORS: Record<string, string> = { standard: PALETTE[0], fast: PALETTE[2], flex: PALETTE[1], unknown: PALETTE[8] }
const MODE_ORDER = ['standard', 'fast', 'flex']

export type TpsStat = 'tokensPerSecond' | 'tokensPerSecondP50' | 'tokensPerSecondP95'

/** Output speed per model, one bar per speed mode, from a `by=modelMode` breakdown (keys are `model|mode`). */
export function ModeComparisonChart({
  rows,
  stat,
  format,
}: {
  rows: BreakdownRow[]
  stat: TpsStat
  format: (v: number) => string
}) {
  const { hidden, toggle } = useHidden()
  const byModel = new Map<string, Record<string, string | number>>()
  const modes = new Set<string>()
  for (const r of rows) {
    const v = r[stat]
    if (v === null || v === undefined) continue
    const cut = r.key.lastIndexOf('|')
    const model = cut > 0 ? r.key.slice(0, cut) : r.key
    const mode = cut > 0 ? r.key.slice(cut + 1) : 'unknown'
    modes.add(mode)
    const row = byModel.get(model) ?? { model }
    row[mode] = v
    row[`${mode}__n`] = r.throughputRequests
    byModel.set(model, row)
  }
  const data = [...byModel.values()]
  if (data.length === 0) return <div className="state muted">No requests with a measured output speed in this range.</div>
  const ordered = [...modes].sort((a, b) => {
    const ia = MODE_ORDER.indexOf(a)
    const ib = MODE_ORDER.indexOf(b)
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.localeCompare(b)
  })
  const height = 48 + data.length * Math.max(36, ordered.length * 16)

  return (
    <div className="chart" style={{ height }}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" margin={{ top: 4, right: 16, bottom: 0, left: 0 }} barGap={2}>
          <CartesianGrid stroke="var(--grid)" horizontal={false} />
          <XAxis type="number" tickFormatter={(v: number) => fmtCompact(v)} tick={axisStyle} stroke="var(--border)" />
          <YAxis type="category" dataKey="model" tick={axisStyle} stroke="var(--border)" width={170} />
          <Tooltip
            contentStyle={tooltipStyle}
            cursor={{ fill: 'var(--surface-2)' }}
            formatter={(v, name, item) => {
              const n = (item.payload as Record<string, number>)[`${String(name)}__n`]
              return [`${typeof v === 'number' ? format(v) : '—'} · ${fmtInt(n)} req`, name]
            }}
          />
          <Legend
            wrapperStyle={{ fontSize: 12, cursor: 'pointer' }}
            onClick={(e) => toggle(e.dataKey)}
            formatter={(value, entry) => <span style={{ opacity: hidden.has(String(entry.dataKey)) ? 0.4 : 1 }}>{value}</span>}
          />
          {ordered.map((m) => (
            <Bar
              key={m}
              dataKey={m}
              name={m}
              fill={MODE_COLORS[m] ?? PALETTE[4]}
              hide={hidden.has(m)}
              radius={[0, 3, 3, 0]}
              maxBarSize={18}
              isAnimationActive={false}
            />
          ))}
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}
