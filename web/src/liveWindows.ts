/** Rolling windows that end now, for the live charts. */
export const LIVE_WINDOWS = [
  { key: '1m', ms: 60_000 },
  { key: '5m', ms: 300_000 },
  { key: '10m', ms: 600_000 },
] as const
export type LiveWindowKey = (typeof LIVE_WINDOWS)[number]['key']

export type LiveSeries = {
  key: string
  label: string
  color: string
  /** [time ms, value], sorted by time. */
  points: Array<[number, number]>
}
