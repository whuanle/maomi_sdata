/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import path from 'node:path';

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, 'src'),
    },
  },
  server: {
    port: 4100,
    proxy: {
      '/api': {
        target: 'http://localhost:5300',
        changeOrigin: false,
      },
      '/rum': {
        target: 'http://localhost:5300',
        changeOrigin: false,
      },
      // RUM 实时通道（SignalR WebSocket，T616）
      '/hub': {
        target: 'http://localhost:5300',
        changeOrigin: false,
        ws: true,
      },
    },
  },
  build: {
    // 构建产物直接输出到 .NET 宿主 wwwroot，同源单机部署
    outDir: '../src/SData/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 2048,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
  },
});
