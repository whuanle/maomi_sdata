import { cn } from '@/utils/cn';

interface AppLogoProps {
  /** 边长（正方形） */
  size?: number;
  alt?: string;
  className?: string;
}

/** 站点 Logo：登录页与侧边栏共用原始品牌图片，主题无关. */
export function AppLogo({ size = 30, alt = 'SData', className }: AppLogoProps) {
  return (
    <img
      src={`${import.meta.env.BASE_URL}logo.png`}
      alt={alt}
      width={size}
      height={size}
      className={cn('shrink-0 select-none rounded-md object-contain', className)}
      draggable={false}
    />
  );
}
