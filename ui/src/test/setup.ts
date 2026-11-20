import '@testing-library/jest-dom/vitest';

// Node 26 的实验性 global localStorage 需要 --localstorage-file 才可用（写入即抛错），
// 这里探测失败时兜底一份内存实现，供 zustand persist 与主题 store 在测试中使用。
class MemoryStorage implements Storage {
  private map = new Map<string, string>();

  get length(): number {
    return this.map.size;
  }

  clear(): void {
    this.map.clear();
  }

  getItem(key: string): string | null {
    return this.map.get(key) ?? null;
  }

  key(index: number): string | null {
    return [...this.map.keys()][index] ?? null;
  }

  removeItem(key: string): void {
    this.map.delete(key);
  }

  setItem(key: string, value: string): void {
    this.map.set(key, value);
  }
}

const probeKey = '__sdata_storage_probe__';
let usable = true;
try {
  globalThis.localStorage.setItem(probeKey, '1');
  globalThis.localStorage.removeItem(probeKey);
} catch {
  usable = false;
}
if (!usable) {
  Object.defineProperty(globalThis, 'localStorage', { value: new MemoryStorage(), configurable: true, writable: true });
}

// jsdom 不实现 matchMedia，antd Grid（响应式断点）渲染时需要
if (typeof window !== 'undefined' && !window.matchMedia) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: (query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => undefined,
      removeListener: () => undefined,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      dispatchEvent: () => false,
    }),
  });
}
