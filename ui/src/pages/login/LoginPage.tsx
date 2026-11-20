import { useForm } from 'react-hook-form';
import { Lock, User } from 'lucide-react';
import { useNavigate } from 'react-router';
import { useTranslation } from 'react-i18next';
import { login } from '@/api/auth';
import { ApiError } from '@/api/client';
import { useAuthStore } from '@/store/auth';
import { AppLogo } from '@/layouts/components/AppLogo';
import { Button, Field, Input, Password, toast } from '@/design-system';

interface LoginFormValues {
  username: string;
  password: string;
}

/** 登录页：全屏纯色背景 + 居中卡片（系统不开放注册，账号由超级管理员创建）. */
export function LoginPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const setAuth = useAuthStore((state) => state.setAuth);
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<LoginFormValues>({ defaultValues: { username: '', password: '' } });

  const onSubmit = handleSubmit(async (values) => {
    try {
      const response = await login(values.username.trim(), values.password);
      setAuth(response.token, response.username, response.role);
      toast.success(t('login.success'));
      navigate('/overview');
    } catch (error) {
      const detail = error instanceof ApiError ? error.business?.message : t('login.failed');
      toast.error(detail ?? t('login.failed'));
    }
  });

  return (
    <div className="flex min-h-screen items-center justify-center bg-bg p-6">
      <div className="w-[400px] rounded-lg border border-border bg-surface p-7 shadow-[var(--c-shadow-sm)]">
        <div className="mb-6 text-center">
          <AppLogo size={48} className="mx-auto mb-3" />
          <div className="mb-1 text-xl font-semibold text-fg">SData</div>
          <div className="text-sm text-muted">{t('app.subtitle')}</div>
        </div>
        <form onSubmit={onSubmit} className="flex flex-col gap-4" autoComplete="off">
          <Field label={t('login.username')} required error={errors.username?.message} htmlFor="login-username">
            <Input
              id="login-username"
              autoFocus
              placeholder={t('login.username')}
              prefix={<User className="size-3.5" aria-hidden />}
              invalid={Boolean(errors.username)}
              {...register('username', { required: t('login.username') ?? '' })}
            />
          </Field>
          <Field label={t('login.password')} required error={errors.password?.message} htmlFor="login-password">
            <Password
              id="login-password"
              placeholder={t('login.password')}
              prefix={<Lock className="size-3.5" aria-hidden />}
              invalid={Boolean(errors.password)}
              {...register('password', { required: t('login.password') ?? '' })}
            />
          </Field>
          <Button type="submit" variant="primary" loading={isSubmitting} block className="h-9 text-sm">
            {t('login.submit')}
          </Button>
          <div className="text-center text-xs text-subtle">{t('login.noRegister')}</div>
        </form>
      </div>
    </div>
  );
}
