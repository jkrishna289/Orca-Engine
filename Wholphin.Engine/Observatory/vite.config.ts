import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// One IIFE bundle with React inlined, no code splitting. The dashboard injects plugin pages into
// its own DOM as a single HTML document (there is no module graph to load from), so anything that
// emits more than one chunk simply would not load.
export default defineConfig({
  plugins: [react()],

  // Library mode leaves process.env.NODE_ENV for the consumer to substitute — but there is no
  // consumer here, the bundle is the deliverable. Without this, React's development build ships:
  // roughly 3x the size, plus every dev-only warning path running in production.
  define: { 'process.env.NODE_ENV': JSON.stringify('production') },

  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'es2020',
    lib: {
      entry: 'src/main.tsx',
      name: 'OrcaObservatory',
      formats: ['iife'],
      fileName: () => 'observatory.js',
    },
    rollupOptions: {
      output: { inlineDynamicImports: true },
    },
  },
});
