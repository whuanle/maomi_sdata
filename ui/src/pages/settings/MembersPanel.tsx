import { useCallback, useEffect, useState } from 'react';
import { KeyRound, Lock, Plus, UserCog } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useForm } from 'react-hook-form';
import { changePassword } from '@/api/auth';
import { createUser, deleteUser, fetchUsers, resetUserPassword, updateUserRole } from '@/api/users';
import type { UserItem } from '@/api/types';
import {
  AuditCell,
  Button,
  DataTable,
  Field,
  Input,
  Modal,
  Password,
  Popconfirm,
  Tag,
  toast,
  type TableColumn,
} from '@/design-system';
import { isAdminRole, useAuthStore } from '@/store/auth';

interface CreateFormValues {
  username: string;
  password: string;
  role: string;
}

interface RoleFormValues {
  role: string;
}

interface ResetFormValues {
  newPassword: string;
}

interface ChangePasswordFormValues {
  oldPassword: string;
  newPassword: string;
}

/** 成员面板（系统设置 → 成员 tab，全员可见）：成员可修改自己密码；创建/删除/重置密码/改权限仅超级管理员. */
export function MembersPanel() {
  const { t } = useTranslation();
  const username = useAuthStore((state) => state.username);
  const role = useAuthStore((state) => state.role);
  const viewerIsAdmin = isAdminRole(role);

  // 四个表单各自独立 useForm（创建成员/改角色/重置密码/修改自己密码）
  const {
    register: registerCreate,
    handleSubmit: submitCreateBase,
    reset: resetCreate,
    watch: watchCreate,
    setValue: setValueCreate,
    formState: { errors: createErrors },
  } = useForm<CreateFormValues>({ defaultValues: { username: '', password: '', role: 'readonly' } });
  const {
    handleSubmit: submitRoleBase,
    watch: watchRole,
    setValue: setValueRole,
  } = useForm<RoleFormValues>({ defaultValues: { role: 'readonly' } });
  const {
    register: registerReset,
    handleSubmit: submitResetBase,
    reset: resetReset,
    formState: { errors: resetErrors },
  } = useForm<ResetFormValues>({ defaultValues: { newPassword: '' } });
  const {
    register: registerPassword,
    handleSubmit: submitPasswordBase,
    reset: resetPassword,
    formState: { errors: passwordErrors },
  } = useForm<ChangePasswordFormValues>({ defaultValues: { oldPassword: '', newPassword: '' } });

  const [data, setData] = useState<UserItem[]>([]);
  const [count, setCount] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);
  const [roleTarget, setRoleTarget] = useState<UserItem | null>(null);
  const [resetTarget, setResetTarget] = useState<UserItem | null>(null);
  const [pwOpen, setPwOpen] = useState(false);

  const load = useCallback(async (targetPage = page) => {
    setLoading(true);
    try {
      const result = await fetchUsers({ page: targetPage, pageSize: 20 });
      setData(result.data);
      setCount(result.count);
    } finally {
      setLoading(false);
    }
  }, [page]);

  useEffect(() => {
    void load(1);
  }, [load]);

  const submitCreate = submitCreateBase(async (values) => {
    await createUser(values.username.trim(), values.password, values.role);
    toast.success(t('common.save'));
    setCreateOpen(false);
    resetCreate();
    await load(1);
  });

  const submitRole = submitRoleBase(async (values) => {
    if (!roleTarget) return;
    await updateUserRole(roleTarget.id, values.role);
    toast.success(t('common.save'));
    setRoleTarget(null);
    await load();
  });

  const submitReset = submitResetBase(async (values) => {
    if (!resetTarget) return;
    await resetUserPassword(resetTarget.id, values.newPassword);
    toast.success(t('common.save'));
    setResetTarget(null);
    resetReset();
  });

  const submitChangePassword = submitPasswordBase(async (values) => {
    await changePassword(values.oldPassword, values.newPassword);
    toast.success(t('common.save'));
    setPwOpen(false);
    resetPassword();
  });

  const removeUser = async (id: number) => {
    await deleteUser(id);
    toast.success(t('common.delete'));
    await load();
  };

  const columns: TableColumn<UserItem>[] = [
    { title: t('users.username'), dataIndex: 'username', width: 180 },
    {
      title: t('users.role'),
      dataIndex: 'role',
      width: 120,
      render: (value: string) => (
        <Tag tone={value === 'admin' ? 'warning' : value === 'write' ? 'info' : 'default'}>{t(`role.${value}`)}</Tag>
      ),
    },
    { title: t('common.createTime'), dataIndex: 'createTime', width: 200, render: (value: string) => <AuditCell time={value} user="-" /> },
    {
      title: t('common.actions'),
      width: 260,
      render: (_, record) => {
        const targetIsAdmin = isAdminRole(record.role);
        const isSelf = record.username === username;
        return (
          <div className="flex flex-wrap items-center gap-1">
            {isSelf && (
              <Button variant="link" size="sm" icon={<Lock className="size-3.5" aria-hidden />} onClick={() => { setPwOpen(true); resetPassword(); }}>
                {t('users.changePassword')}
              </Button>
            )}
            {viewerIsAdmin && !targetIsAdmin && (
              <Button variant="link" size="sm" icon={<UserCog className="size-3.5" aria-hidden />} onClick={() => { setRoleTarget(record); setValueRole('role', record.role); }}>
                {t('users.changeRole')}
              </Button>
            )}
            {viewerIsAdmin && (
              <Button variant="link" size="sm" icon={<KeyRound className="size-3.5" aria-hidden />} onClick={() => { setResetTarget(record); resetReset(); }}>
                {t('users.resetPassword')}
              </Button>
            )}
            {viewerIsAdmin && !targetIsAdmin && !isSelf && (
              <Popconfirm
                title={t('common.confirmDelete')}
                okText={t('common.delete')}
                cancelText={t('common.cancel')}
                onConfirm={() => void removeUser(record.id)}
              >
                <Button variant="link" size="sm" danger>{t('common.delete')}</Button>
              </Popconfirm>
            )}
          </div>
        );
      },
    },
  ];

  return (
    <>
      {viewerIsAdmin && (
        <div className="mb-3">
          <Button variant="primary" icon={<Plus className="size-3.5" aria-hidden />} onClick={() => { resetCreate(); setCreateOpen(true); }}>
            {t('users.create')}
          </Button>
        </div>
      )}
      <DataTable<UserItem>
        rowKey="id"
        loading={loading}
        dataSource={data}
        columns={columns}
        pagination={{ current: page, pageSize: 20, total: count, onChange: (target) => { setPage(target); void load(target); } }}
      />

      {/* 创建成员弹窗 */}
      <Modal
        open={createOpen}
        onOpenChange={(open) => (!open ? setCreateOpen(false) : undefined)}
        title={t('users.create')}
        footer={(
          <>
            <Button onClick={() => setCreateOpen(false)}>{t('common.cancel')}</Button>
            <Button variant="primary" onClick={() => void submitCreate()}>{t('common.save')}</Button>
          </>
        )}
      >
        <form className="flex flex-col gap-3" onSubmit={(event) => { event.preventDefault(); void submitCreate(); }}>
          <Field label={t('users.username')} required error={createErrors.username?.message}>
            <Input
              autoComplete="off"
              invalid={Boolean(createErrors.username)}
              {...registerCreate('username', {
                required: t('users.usernameRule') ?? '',
                pattern: { value: /^[a-zA-Z0-9_.-]{2,64}$/, message: t('users.usernameRule') ?? '' },
              })}
            />
          </Field>
          <Field label={t('users.password')} required error={createErrors.password?.message}>
            <Password
              autoComplete="new-password"
              invalid={Boolean(createErrors.password)}
              {...registerCreate('password', {
                required: t('users.passwordRule') ?? '',
                minLength: { value: 8, message: t('users.passwordRule') ?? '' },
                maxLength: { value: 128, message: t('users.passwordRule') ?? '' },
              })}
            />
          </Field>
          <Field label={t('users.role')} required>
            <div className="flex items-center gap-4 pt-1.5 text-sm text-fg">
              <label className="inline-flex cursor-pointer select-none items-center gap-1.5">
                <input
                  type="radio"
                  name="create-member-role"
                  checked={watchCreate('role') === 'readonly'}
                  onChange={() => setValueCreate('role', 'readonly')}
                  className="size-3.5 accent-primary"
                />
                {t('role.readonly')}
              </label>
              <label className="inline-flex cursor-pointer select-none items-center gap-1.5">
                <input
                  type="radio"
                  name="create-member-role"
                  checked={watchCreate('role') === 'write'}
                  onChange={() => setValueCreate('role', 'write')}
                  className="size-3.5 accent-primary"
                />
                {t('role.write')}
              </label>
            </div>
          </Field>
        </form>
      </Modal>

      {/* 改权限弹窗 */}
      <Modal
        open={roleTarget !== null}
        onOpenChange={(open) => (!open ? setRoleTarget(null) : undefined)}
        title={`${t('users.changeRole')} · ${roleTarget?.username ?? ''}`}
        footer={(
          <>
            <Button onClick={() => setRoleTarget(null)}>{t('common.cancel')}</Button>
            <Button variant="primary" onClick={() => void submitRole()}>{t('common.save')}</Button>
          </>
        )}
      >
        <form className="flex flex-col gap-3">
          <Field label={t('users.role')} required>
            <div className="flex items-center gap-4 pt-1.5 text-sm text-fg">
              <label className="inline-flex cursor-pointer select-none items-center gap-1.5">
                <input
                  type="radio"
                  name="change-role"
                  checked={watchRole('role') === 'readonly'}
                  onChange={() => setValueRole('role', 'readonly')}
                  className="size-3.5 accent-primary"
                />
                {t('role.readonly')}
              </label>
              <label className="inline-flex cursor-pointer select-none items-center gap-1.5">
                <input
                  type="radio"
                  name="change-role"
                  checked={watchRole('role') === 'write'}
                  onChange={() => setValueRole('role', 'write')}
                  className="size-3.5 accent-primary"
                />
                {t('role.write')}
              </label>
            </div>
          </Field>
        </form>
      </Modal>

      {/* 重置密码弹窗 */}
      <Modal
        open={resetTarget !== null}
        onOpenChange={(open) => (!open ? setResetTarget(null) : undefined)}
        title={`${t('users.resetPassword')} · ${resetTarget?.username ?? ''}`}
        footer={(
          <>
            <Button onClick={() => setResetTarget(null)}>{t('common.cancel')}</Button>
            <Button variant="primary" onClick={() => void submitReset()}>{t('common.save')}</Button>
          </>
        )}
      >
        <form className="flex flex-col gap-3">
          <Field label={t('users.newPassword')} required error={resetErrors.newPassword?.message}>
            <Password
              autoComplete="new-password"
              invalid={Boolean(resetErrors.newPassword)}
              {...registerReset('newPassword', {
                required: t('users.passwordRule') ?? '',
                minLength: { value: 8, message: t('users.passwordRule') ?? '' },
                maxLength: { value: 128, message: t('users.passwordRule') ?? '' },
              })}
            />
          </Field>
        </form>
      </Modal>

      {/* 修改自己密码弹窗 */}
      <Modal
        open={pwOpen}
        onOpenChange={(open) => (!open ? setPwOpen(false) : undefined)}
        title={t('users.changePassword')}
        footer={(
          <>
            <Button onClick={() => setPwOpen(false)}>{t('common.cancel')}</Button>
            <Button variant="primary" onClick={() => void submitChangePassword()}>{t('common.save')}</Button>
          </>
        )}
      >
        <form className="flex flex-col gap-3">
          <Field label={t('users.oldPassword')} required error={passwordErrors.oldPassword?.message}>
            <Password
              autoComplete="current-password"
              invalid={Boolean(passwordErrors.oldPassword)}
              {...registerPassword('oldPassword', { required: t('users.oldPassword') ?? '' })}
            />
          </Field>
          <Field label={t('users.newPassword')} required error={passwordErrors.newPassword?.message}>
            <Password
              autoComplete="new-password"
              invalid={Boolean(passwordErrors.newPassword)}
              {...registerPassword('newPassword', {
                required: t('users.passwordRule') ?? '',
                minLength: { value: 8, message: t('users.passwordRule') ?? '' },
                maxLength: { value: 128, message: t('users.passwordRule') ?? '' },
              })}
            />
          </Field>
        </form>
      </Modal>
    </>
  );
}
