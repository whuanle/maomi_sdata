import { useMemo, useState } from 'react';
import dayjs, { type Dayjs } from 'dayjs';
import * as RPopover from '@radix-ui/react-popover';
import { useTranslation } from 'react-i18next';
import { CalendarClock, ChevronDown, X, Zap } from 'lucide-react';
import { cn } from '@/utils/cn';
import { parseNaturalDate } from '@/utils/naturalDate';
import { Button } from './Button';
import { Select } from './Select';
import { Switch } from './Switch';

export interface TimeRange {
  /** ISO 字符串（含时区偏移） */
  from: string;
  to: string;
  /** 命中预设时的 key（'15m'/'1h'…，自定义区间缺省） */
  preset?: string;
  /** Live Tail 滑窗（轮询时终点持续前移到 now） */
  live?: boolean;
}

export interface TimeRangePreset {
  key: string;
  label?: string;
  /** 相对时长（毫秒） */
  ms: number;
}

/** 内置预设：与 /search 对齐（HyperDX 式「最近 N」相对范围列表）. */
export const TIME_RANGE_PRESETS: TimeRangePreset[] = [
  { key: '1m', ms: 60_000 },
  { key: '5m', ms: 5 * 60_000 },
  { key: '15m', ms: 15 * 60_000 },
  { key: '30m', ms: 30 * 60_000 },
  { key: '45m', ms: 45 * 60_000 },
  { key: '1h', ms: 60 * 60_000 },
  { key: '3h', ms: 3 * 60 * 60_000 },
  { key: '6h', ms: 6 * 60 * 60_000 },
  { key: '12h', ms: 12 * 60 * 60_000 },
  { key: '1d', ms: 24 * 60 * 60_000 },
  { key: '2d', ms: 2 * 24 * 60 * 60_000 },
  { key: '7d', ms: 7 * 24 * 60 * 60_000 },
];

/** 相对预设的 i18n 标签键（search.range{Key}）. */
const PRESET_LABEL_KEYS: Record<string, string> = {
  '1m': 'search.range1m',
  '5m': 'search.range5m',
  '15m': 'search.range15m',
  '30m': 'search.range30m',
  '45m': 'search.range45m',
  '1h': 'search.range1h',
  '3h': 'search.range3h',
  '6h': 'search.range6h',
  '12h': 'search.range12h',
  '1d': 'search.range1d',
  '2d': 'search.range2d',
  '7d': 'search.range7d',
};

/** 「某个时间前后」可选时长（时间点 ± 时长，ClickStack 式 Duration）. */
const AROUND_DURATIONS: TimeRangePreset[] = [
  { key: '15m', ms: 15 * 60_000 },
  { key: '30m', ms: 30 * 60_000 },
  { key: '1h', ms: 60 * 60_000 },
  { key: '3h', ms: 3 * 60 * 60_000 },
  { key: '6h', ms: 6 * 60 * 60_000 },
  { key: '12h', ms: 12 * 60 * 60_000 },
  { key: '1d', ms: 24 * 60 * 60_000 },
];

/** 右栏页签（ClickStack 式：绝对区间 / 某个时间前后）. */
type PanelTab = 'range' | 'around';

export interface TimeRangePickerProps {
  value: TimeRange;
  onChange: (range: TimeRange) => void;
  /** 预设列表（默认 TIME_RANGE_PRESETS） */
  presets?: TimeRangePreset[];
  align?: 'start' | 'center' | 'end';
  /** 测试定位锚点（透传到触发器 data-testid） */
  testId?: string;
  className?: string;
  allowLive?: boolean;
  showAbsoluteRange?: boolean;
}

function formatDraft(value: Dayjs): string {
  return value.format('YYYY-MM-DD HH:mm');
}

/**
 * 时间范围选择器（ClickStack 式组合控件）：触发器一体承担「范围 + Live Tail」展示，
 * 弹层左栏 Live Tail + 相对范围列表（最近 N = 多久到现在），右栏页签为
 * 「Time range（起止区间）」/「Around a time（某时间点 ± 时长）」+ Apply；
 * 输入框支持自然语言（昨天/1h ago/now 等，见 parseNaturalDate），
 * 头部 1h back/forward 将当前区间整体平移 1 小时。受控组件。
 */
export function TimeRangePicker({
  value,
  onChange,
  presets = TIME_RANGE_PRESETS,
  align = 'start',
  testId,
  className,
  allowLive = true,
  showAbsoluteRange = false,
}: TimeRangePickerProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [tab, setTab] = useState<PanelTab>('range');
  // 自定义编辑态（打开面板时从当前值回填；允许自然语言输入）
  const [draftFrom, setDraftFrom] = useState('');
  const [draftTo, setDraftTo] = useState('');
  const [aroundTime, setAroundTime] = useState('');
  const [aroundMs, setAroundMs] = useState(AROUND_DURATIONS[0].ms);
  const [parseError, setParseError] = useState(false);

  const hitPreset = useMemo(() => presets.find((preset) => preset.key === value.preset), [presets, value.preset]);
  const isLive = value.live === true;

  const label = isLive
    ? t('search.liveTail')
    : hitPreset && !showAbsoluteRange
      ? hitPreset.label ?? hitPreset.key
      : `${dayjs(value.from).format('MM-DD HH:mm')} ~ ${dayjs(value.to).format('MM-DD HH:mm')}`;

  const openPanel = () => {
    setDraftFrom(formatDraft(dayjs(value.from)));
    setDraftTo(formatDraft(dayjs(value.to)));
    setAroundTime(formatDraft(dayjs(value.to)));
    setParseError(false);
    setTab('range');
    setOpen(true);
  };

  /** 以 now 为终点落一份相对窗口（Live Tail / 相对预设共用）. */
  const applyRelative = (preset: TimeRangePreset, live: boolean) => {
    const to = dayjs();
    onChange({ from: to.subtract(preset.ms, 'millisecond').toISOString(), to: to.toISOString(), preset: preset.key, live });
  };

  const pickLive = () => {
    // Live Tail 回看窗口取 15m（HyperDX 式默认短窗滑窗轮询）
    const preset = presets.find((item) => item.key === '15m') ?? presets[0];
    applyRelative(preset, true);
    setOpen(false);
  };

  const toggleLive = (on: boolean) => {
    if (on) {
      const preset = (value.preset ? presets.find((item) => item.key === value.preset) : undefined) ?? presets.find((item) => item.key === '15m') ?? presets[0];
      applyRelative(preset, true);
      return;
    }

    // 关闭 Live Tail：保持当前窗口长度，锚定为刚过去的静态区间
    const spanMs = Math.max(60_000, dayjs(value.to).diff(dayjs(value.from)));
    const to = dayjs();
    onChange({ from: to.subtract(spanMs, 'millisecond').toISOString(), to: to.toISOString(), preset: value.preset, live: false });
  };

  const pickPreset = (preset: TimeRangePreset) => {
    applyRelative(preset, false);
    setOpen(false);
  };

  /** 解析草稿输入失败时标红提示. */
  const parseDraft = (input: string): Dayjs | null => {
    const parsed = parseNaturalDate(input);
    if (parsed === null) {
      setParseError(true);
      return null;
    }

    return parsed;
  };

  const applyRange = () => {
    setParseError(false);
    const from = parseDraft(draftFrom);
    const to = parseDraft(draftTo);
    if (from === null || to === null || !from.isBefore(to)) {
      setParseError(true);
      return;
    }

    onChange({ from: from.toISOString(), to: to.toISOString() });
    setOpen(false);
  };

  const applyAround = () => {
    setParseError(false);
    const anchor = parseDraft(aroundTime);
    if (anchor === null) {
      return;
    }

    onChange({
      from: anchor.subtract(aroundMs, 'millisecond').toISOString(),
      to: anchor.add(aroundMs, 'millisecond').toISOString(),
    });
    setOpen(false);
  };

  /** 1h back/forward：当前区间整体平移 1 小时并即时生效（面板保持打开，ClickStack 式）. */
  const shift1h = (direction: 1 | -1) => {
    const from = parseNaturalDate(draftFrom) ?? dayjs(value.from);
    const to = parseNaturalDate(draftTo) ?? dayjs(value.to);
    const shiftedFrom = from.add(direction, 'hour');
    const shiftedTo = to.add(direction, 'hour');
    setDraftFrom(formatDraft(shiftedFrom));
    setDraftTo(formatDraft(shiftedTo));
    setAroundTime(formatDraft(direction > 0 ? shiftedTo : shiftedFrom));
    setParseError(false);
    onChange({ from: shiftedFrom.toISOString(), to: shiftedTo.toISOString() });
  };

  const draftInputClass = 'h-8 w-full rounded-sm border border-border bg-surface px-2 font-mono text-xs text-fg outline-none focus:border-primary';

  return (
    <RPopover.Root open={open} onOpenChange={(next) => (next ? openPanel() : setOpen(false))}>
      <RPopover.Trigger asChild>
        <Button
          variant={isLive ? 'accentOutline' : 'secondary'}
          testId={testId}
          icon={isLive ? <Zap className="size-3.5" aria-hidden /> : <CalendarClock className="size-3.5 text-subtle" aria-hidden />}
          className={cn('font-mono text-[13px]', className)}
          aria-label="time range"
        >
          <span className="truncate">{label}</span>
          <ChevronDown className="size-3.5 shrink-0 text-subtle" aria-hidden />
        </Button>
      </RPopover.Trigger>
      <RPopover.Portal>
        <RPopover.Content
          align={align}
          sideOffset={6}
          className="sdata-pop z-[1200] w-[420px] overflow-hidden rounded-lg border border-border bg-surface shadow-[var(--c-shadow)]"
        >
          {/* 头部：Live Tail 开关 + 1h back/forward 平移 + 关闭（ClickStack 式「Live tail ranges」条） */}
          <div className="flex items-center justify-between gap-2 border-b border-border px-3 py-2">
            {allowLive && <label className="flex cursor-pointer items-center gap-2">
              <Switch checked={isLive} onChange={toggleLive} aria-label={t('search.liveTail')} />
              <span className="text-xs font-medium text-fg">{t('search.liveTailRanges')}</span>
            </label>}
            <div className="ml-auto flex items-center gap-1">
              <button
                type="button"
                onClick={() => shift1h(-1)}
                className="cursor-pointer rounded-sm border border-border bg-surface px-1.5 py-0.5 font-mono text-[11px] text-muted transition-colors hover:bg-surface-2 hover:text-fg"
              >
                {t('search.oneHourBack')}
              </button>
              <button
                type="button"
                onClick={() => shift1h(1)}
                className="cursor-pointer rounded-sm border border-border bg-surface px-1.5 py-0.5 font-mono text-[11px] text-muted transition-colors hover:bg-surface-2 hover:text-fg"
              >
                {t('search.oneHourForward')}
              </button>
              <button
                type="button"
                aria-label="close"
                onClick={() => setOpen(false)}
                className="cursor-pointer rounded-sm p-1 text-subtle transition-colors hover:bg-surface-2 hover:text-fg"
              >
                <X className="size-3.5" aria-hidden />
              </button>
            </div>
          </div>

          <div className="flex">
            {/* 左栏：Live Tail + 相对范围列表（多久到现在） */}
            <div className="w-[150px] shrink-0 border-r border-border py-1.5">
              {allowLive && <><button
                type="button"
                onClick={pickLive}
                className={cn(
                  'flex w-full cursor-pointer items-center px-3 py-1.5 text-left text-[13px] transition-colors',
                  isLive ? 'bg-accent-soft font-medium text-accent' : 'text-fg hover:bg-surface-2',
                )}
              >
                <Zap className="mr-1.5 size-3.5 shrink-0" aria-hidden />
                {t('search.liveTail')}
              </button>
              <div className="my-1 border-t border-border/60" /></>}
              {presets.map((preset) => {
                const active = !isLive && value.preset === preset.key;
                return (
                  <button
                    key={preset.key}
                    type="button"
                    onClick={() => pickPreset(preset)}
                    className={cn(
                      'block w-full cursor-pointer px-3 py-1.5 text-left font-mono text-[13px] transition-colors',
                      active ? 'bg-primary-soft text-primary' : 'text-fg hover:bg-surface-2',
                    )}
                  >
                    {PRESET_LABEL_KEYS[preset.key] ? t(PRESET_LABEL_KEYS[preset.key]) : preset.label ?? preset.key}
                  </button>
                );
              })}
            </div>

            {/* 右栏：Time range / Around a time 页签 + 自然语言输入 + Apply */}
            <div className="flex min-w-0 flex-1 flex-col p-3">
              <div className="mb-2.5 grid grid-cols-2 gap-1.5">
                <button
                  type="button"
                  onClick={() => setTab('range')}
                  className={cn(
                    'cursor-pointer rounded-sm border px-2 py-1 text-xs transition-colors',
                    tab === 'range' ? 'border-primary bg-primary-soft font-medium text-primary' : 'border-border bg-surface text-muted hover:bg-surface-2',
                  )}
                >
                  {t('search.timeRange')}
                </button>
                <button
                  type="button"
                  onClick={() => setTab('around')}
                  className={cn(
                    'cursor-pointer rounded-sm border px-2 py-1 text-xs transition-colors',
                    tab === 'around' ? 'border-primary bg-primary-soft font-medium text-primary' : 'border-border bg-surface text-muted hover:bg-surface-2',
                  )}
                >
                  {t('search.aroundATime')}
                </button>
              </div>

              {tab === 'range' ? (
                <>
                  <label className="mb-1 block text-xs text-subtle" htmlFor="sdata-range-from">{t('search.startTime')}</label>
                  <input
                    id="sdata-range-from"
                    type="text"
                    value={draftFrom}
                    onChange={(event) => setDraftFrom(event.target.value)}
                    placeholder="now / yesterday / 1h ago"
                    className={cn(draftInputClass, parseError && !parseNaturalDate(draftFrom) && 'border-error')}
                  />
                  <label className="mb-1 mt-2.5 block text-xs text-subtle" htmlFor="sdata-range-to">{t('search.endTime')}</label>
                  <input
                    id="sdata-range-to"
                    type="text"
                    value={draftTo}
                    onChange={(event) => setDraftTo(event.target.value)}
                    placeholder="now / 2026-11-17 14:05"
                    className={cn(draftInputClass, parseError && !parseNaturalDate(draftTo) && 'border-error')}
                  />
                </>
              ) : (
                <>
                  <label className="mb-1 block text-xs text-subtle" htmlFor="sdata-around-time">{t('search.time')}</label>
                  <input
                    id="sdata-around-time"
                    type="text"
                    value={aroundTime}
                    onChange={(event) => setAroundTime(event.target.value)}
                    placeholder="now / 2026-11-17 14:05"
                    className={cn(draftInputClass, parseError && !parseNaturalDate(aroundTime) && 'border-error')}
                  />
                  <label className="mb-1 mt-2.5 block text-xs text-subtle" htmlFor="sdata-around-duration">{t('search.duration')}</label>
                  <div className="w-full">
                    <Select
                      value={String(aroundMs)}
                      onValueChange={(next) => setAroundMs(Number(next))}
                      aria-label="around duration"
                      options={AROUND_DURATIONS.map((duration) => ({ value: String(duration.ms), label: duration.key }))}
                    />
                  </div>
                </>
              )}

              <div className="mt-2 text-[11px] leading-relaxed text-faint">{t('search.naturalDateHint')}</div>
              {parseError && <div className="mt-1 text-[11px] text-error">{t('search.badTimeInput')}</div>}
              <div className="mt-3 flex justify-end">
                <Button size="sm" variant="primary" onClick={tab === 'range' ? applyRange : applyAround}>
                  {t('common.apply')}
                </Button>
              </div>
            </div>
          </div>
        </RPopover.Content>
      </RPopover.Portal>
    </RPopover.Root>
  );
}
