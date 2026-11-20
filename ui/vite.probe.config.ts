import { defineConfig } from 'vite';
import path from 'node:path';

export default defineConfig({
  build: {
    outDir: '../src/SData/wwwroot/rum',
    emptyOutDir: false,
    lib: { entry: path.resolve(__dirname, 'packages/rum-sdk/src/browser.ts'), name: 'SDataRum', formats: ['iife'], fileName: () => 'probe.js' },
    rollupOptions: { output: { inlineDynamicImports: true } },
  },
});
