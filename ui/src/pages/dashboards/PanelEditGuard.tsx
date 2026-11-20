import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useBlocker } from 'react-router';
import { useTranslation } from 'react-i18next';
import { ConfirmModal } from '@/design-system';

/** 编辑器交互后离开需确认；只记脏标识，不把查询内容写入地址或持久存储. */
export function PanelEditGuard({ children }: { children: (allowLeave: () => void) => ReactNode }) {
  const { t } = useTranslation();
  const [touched, setTouched] = useState(false);
  const allowed = useRef(false);
  const blocker = useBlocker(({ currentLocation, nextLocation }) => touched && !allowed.current && (currentLocation.pathname !== nextLocation.pathname || new URLSearchParams(currentLocation.search).get('kind') !== new URLSearchParams(nextLocation.search).get('kind')));
  useEffect(() => {
    if (!touched) return;
    const leave = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', leave);
    return () => window.removeEventListener('beforeunload', leave);
  }, [touched]);
  return <div onChangeCapture={() => setTouched(true)} onKeyDownCapture={() => setTouched(true)} onPointerDownCapture={() => setTouched(true)}>
    {children(() => { allowed.current = true; })}
    <ConfirmModal open={blocker.state === 'blocked'} onOpenChange={open => { if (!open && blocker.state === 'blocked') blocker.reset(); }} title={t('dashboard.unsaved')} content={t('dashboard.discardHint')} okText={t('dashboard.discard')} cancelText={t('common.cancel')} onOk={() => { if (blocker.state === 'blocked') blocker.proceed(); }} />
  </div>;
}
