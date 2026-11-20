import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import i18n from '@/i18n';
import { LUCENE_HELP_EXAMPLES, luceneHelpExamples, LuceneSearchHelp } from '../LuceneSearchHelp';
import { compileLucene } from '../lucene';

afterEach(async () => { await act(async () => { await i18n.changeLanguage('zh-CN'); }); });

describe('LuceneSearchHelp', () => {
  it.each(['zh-CN', 'en-US'])('默认显示示例，可折叠且完整展示当前草稿（%s）', async (language) => {
    await act(async () => { await i18n.changeLanguage(language); });
    const { rerender } = render(<LuceneSearchHelp source="logs" value="" />);
    expect(screen.getByText(i18n.t('search.luceneHelp.empty'))).toBeTruthy();
    expect(screen.getByTestId('search-lucene-help').textContent).toContain(i18n.t('search.luceneHelp.scope.logs'));
    expect(screen.getByTestId('search-lucene-help').textContent).toContain(i18n.t('search.luceneHelp.existence'));
    for (const { value } of LUCENE_HELP_EXAMPLES) expect(screen.getByText(value)).toBeTruthy();
    const toggle = screen.getByRole('button', { name: i18n.t('search.luceneHelp.title') });
    const contentId = toggle.getAttribute('aria-controls')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(document.getElementById(contentId)).not.toBeNull();
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(document.getElementById(contentId)).toBeNull();

    const draft = 'service:"api gateway" <script>alert(1)</script>';
    rerender(<LuceneSearchHelp source="traces" value={draft} />);
    expect(screen.getByText(draft)).toBeTruthy();
    expect(screen.getByText(i18n.t('search.luceneHelp.draft'))).toBeTruthy();
    expect(screen.queryByTestId('search-lucene-help')?.querySelector('script')).toBeNull();
    fireEvent.click(toggle);
    expect(screen.getByTestId('search-lucene-help').textContent).toContain(i18n.t('search.luceneHelp.scope.traces'));
    expect(screen.getByTestId('search-lucene-help').textContent).not.toContain(i18n.t('search.luceneHelp.scope.logs'));
  });

  it.each(['zh-CN', 'en-US'])('图表说明按来源适配且多实例折叠互不影响（%s）', async (language) => {
    await act(async () => { await i18n.changeLanguage(language); });
    const { rerender } = render(<><LuceneSearchHelp source="metrics" value="value:>80" context="chart" /><LuceneSearchHelp source="sessions" value="" context="chart" /></>);
    const regions = screen.getAllByTestId('chart-lucene-help');
    expect(regions[0].textContent).toContain('name:cpu*');
    expect(regions[1].textContent).toContain('domain:api*');
    for (const region of regions) {
      expect(region.textContent).not.toContain(i18n.t('search.luceneHelp.existence'));
      expect(region.textContent).toContain(i18n.t('search.luceneHelp.chart.existence'));
    }
    const toggles = screen.getAllByRole('button', { name: i18n.t('search.luceneHelp.title') });
    expect(toggles[0].getAttribute('aria-controls')).not.toBe(toggles[1].getAttribute('aria-controls'));
    fireEvent.click(toggles[0]);
    expect(toggles[0].getAttribute('aria-expanded')).toBe('false');
    expect(toggles[1].getAttribute('aria-expanded')).toBe('true');
    rerender(<LuceneSearchHelp source="sessions" value="Chrome" context="chart" />);
    fireEvent.click(screen.getByRole('button', { name: i18n.t('search.luceneHelp.title') }));
    expect(screen.getByTestId('chart-lucene-help').textContent).not.toContain('name:cpu');
    expect(screen.getByTestId('chart-lucene-help').textContent).toContain('domain:api*');
  });

  it.each(['logs', 'traces', 'metrics', 'sessions'] as const)('所有展示示例均能编译为绑定参数（%s）', (source) => {
    for (const { value } of luceneHelpExamples(source)) {
      const result = compileLucene(value, source);
      expect(Object.keys(result.params).length).toBeGreaterThan(0);
      expect(result.sql).toContain('$p0');
    }
  });
});
