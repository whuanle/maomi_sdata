import { TIME_RANGE_PRESETS, type TimeRange } from '@/design-system';

export interface MapState {
  range: TimeRange;
  service: string;
  query: string;
  syntax: 'sql' | 'lucene';
  sampling: number;
}
export function resolveRange(range: TimeRange): TimeRange {
  const preset = TIME_RANGE_PRESETS.find((item) => item.key === range.preset);
  return preset ? { from: new Date(Date.now() - preset.ms).toISOString(), to: new Date().toISOString(), preset: preset.key } : range;
}
export function parseMapState(params: URLSearchParams): MapState {
  const preset = params.get('preset') ?? '1h';
  const from = params.get('from'), to = params.get('to');
  const range = from && to && Number.isFinite(Date.parse(from)) && Date.parse(from) < Date.parse(to)
    ? { from, to } : resolveRange({ from: '', to: '', preset: TIME_RANGE_PRESETS.some((item) => item.key === preset) ? preset : '1h' });
  const sampling = Number(params.get('sampling') ?? 100);
  return { range, service: params.get('service') ?? '', query: params.get('q') ?? '', syntax: params.get('syntax') === 'sql' ? 'sql' : 'lucene',
    sampling: Number.isInteger(sampling) && sampling >= 1 && sampling <= 100 ? sampling : 100 };
}
export function serializeMapState(state: MapState) {
  return new URLSearchParams({ ...(state.range.preset ? { preset: state.range.preset } : { from: state.range.from, to: state.range.to }),
    service: state.service, q: state.query, syntax: state.syntax, sampling: String(state.sampling) });
}
