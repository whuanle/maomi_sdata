import { useCallback, useEffect, useMemo, useState } from 'react';
import { FolderOpen, Search } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { deleteSavedQuery, listSavedQueries } from '@/api/analytics';
import type { SavedQueryItem, SavedSearchDefinition, SearchDomain } from '@/api/types';
import { Button, Empty, Input, Page, Popconfirm, QueryBar, Spin, Tag, toast } from '@/design-system';
import { canWriteRole, useAuthStore } from '@/store/auth';
import { formatDateTime } from '@/utils/format';

/** 快照 domain → 数据源标签文案（definition 损坏时回退 '-';返回 null 表示无法解析）. */
function domainOf(definitionJson: string): SearchDomain | null {
  try {
    const definition = JSON.parse(definitionJson) as SavedSearchDefinition;
    return definition.domain === 'traces' ? 'traces' : definition.domain === 'logs' ? 'logs' : null;
  } catch {
    return null;
  }
}

/** 卡片预览文案：快照行2 条件（Lucene 表达式 / SQL WHERE 片段；历史整句 SQL 回退显首行）. */
function previewOf(definitionJson: string): string {
  try {
    const definition = JSON.parse(definitionJson) as SavedSearchDefinition;
    if (definition.mode === 'sql') {
      return definition.where || '-';
    }

    if (definition.mode === 'lucene' || definition.where !== undefined) {
      return definition.where || '-';
    }

    return (definition.sql ?? definition.query ?? '').split('\n')[0] || '-';
  } catch {
    return '-';
  }
}

/** 已保存搜索页：搜索页保存的命名快照列表（打开 = 以同一 SearchPage 组件呈现对应画面）. */
export function SavedSearchesPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const canWrite = useAuthStore((state) => canWriteRole(state.role));
  const [data, setData] = useState<SavedQueryItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [keyword, setKeyword] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const result = await listSavedQueries({ kind: 'search', page: 1, pageSize: 100 });
      setData(result.data);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const items = useMemo(() => {
    const word = keyword.trim().toLowerCase();
    if (word.length === 0) {
      return data;
    }

    return data.filter((item) => item.name.toLowerCase().includes(word));
  }, [data, keyword]);

  const remove = async (id: number | string) => {
    try {
      await deleteSavedQuery(id);
      await load();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : String(err));
    }
  };

  return (
    <Page
      extra={(
        <QueryBar className="w-full">
          <Input
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
            placeholder={t('savedSearches.filterPlaceholder')}
            className="w-[200px]"
            testId="saved-searches-filter"
          />
          <Button variant="primary" icon={<Search className="size-3.5" aria-hidden />} onClick={() => navigate('/search')}>
            {t('savedSearches.newSearch')}
          </Button>
        </QueryBar>
      )}
    >
      {loading && items.length === 0 ? (
        <div className="flex h-full items-center justify-center"><Spin /></div>
      ) : (
        <div className="flex h-full flex-col gap-4 overflow-auto pb-4">
          <div className="grid grid-cols-3 gap-4">
            {items.map((item) => {
              const domain = domainOf(item.definition);
              return (
                <div key={item.id} className="flex flex-col rounded-lg border border-border bg-surface">
                  <div className="flex items-center gap-2 border-b border-border px-4 pt-3 pb-2">
                    {domain && <Tag tone={domain === 'traces' ? 'info' : 'default'}>{domain === 'traces' ? t('search.sourceTraces') : t('search.sourceLogs')}</Tag>}
                    <span className="truncate text-sm font-medium text-fg">{item.name}</span>
                  </div>
                  <div className="min-h-10 flex-1 truncate px-4 py-2 font-mono text-xs text-muted" title={previewOf(item.definition)}>
                    {previewOf(item.definition)}
                  </div>
                  <div className="flex flex-wrap items-center gap-1 px-3 pb-1">
                    <Button
                      variant="link"
                      size="sm"
                      icon={<FolderOpen className="size-3" aria-hidden />}
                      onClick={() => navigate(`/saved-searches/${item.exactId ?? item.id}`)}
                    >
                      {t('savedSearches.open')}
                    </Button>
                    {canWrite && (
                      <Popconfirm
                        title={t('common.confirmDelete')}
                        okText={t('common.delete')}
                        cancelText={t('common.cancel')}
                        onConfirm={() => void remove(item.exactId ?? item.id)}
                      >
                        <Button variant="link" size="sm" danger>{t('common.delete')}</Button>
                      </Popconfirm>
                    )}
                  </div>
                  <div className="px-4 pb-2.5 text-xs text-subtle">
                    {`${t('savedSearches.updatedAt')} ${formatDateTime(item.updateTime)}`}
                  </div>
                </div>
              );
            })}
          </div>
          {items.length === 0 && (
            <Empty description={keyword.trim().length > 0 ? t('common.empty') : t('savedSearches.emptyHint')} />
          )}
        </div>
      )}
    </Page>
  );
}
