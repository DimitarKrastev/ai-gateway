import { Area, AreaChart, CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { Timeseries } from '../types'
import { fmtAxisTime, fmtDateTime } from '../format'
import { PALETTE } from '../metrics'

const axisStyle = { fontSize: 11, fill: 'var(--muted)' }

export function TimeChart({
  data,
  format,
  mode,
  height = 260,
  labels,
}: {
  data: Timeseries
  format: (v: number) => string
  /** stack = stacked areas (sums), line = independent lines (averages, peaks) */
  mode: 'stack' | 'line'
  height?: number
  /** Overrides series labels (e.g. metric names when several metrics are plotted). */
  labels?: Record<string, string>
}) {
  if (data.points.length === 0 || data.series.length === 0)
    return <div className="state muted" style={{ height }}>No data in this range.</div>

  const series = data.series.map((s, i) => ({
    ...s,
    name: labels?.[s.key] ?? s.label,
    color: s.label.startsWith('Other') ? PALETTE[8] : PALETTE[i % 8],
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
  const yAxis = <YAxis tickFormatter={(v: number) => format(v)} tick={axisStyle} stroke="var(--border)" width={64} />
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
              <Line key={s.key} dataKey={s.key} name={s.name} stroke={s.color} dot={false} strokeWidth={2} connectNulls isAnimationActive={false} />
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
