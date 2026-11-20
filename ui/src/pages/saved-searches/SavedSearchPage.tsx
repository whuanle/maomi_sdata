import { Navigate, useParams } from 'react-router';
import { SearchPage } from '@/pages/search/SearchPage';

/**
 * 已保存搜索打开页：直接复用统一搜索页组件（同一组件实例而非复制，搜索页后续变化自动同步）。
 * savedId 注入后 SearchPage 进入已保存搜索上下文：打开即应用快照，保存即更新当前条目。
 */
export function SavedSearchPage() {
  const { savedId } = useParams();
  const id = savedId ?? '';
  if (!/^[1-9]\d*$/.test(id)) {
    return <Navigate to="/saved-searches" replace />;
  }

  return <SearchPage savedId={id} />;
}
