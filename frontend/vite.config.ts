import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Dev workflow: `npm run dev` serves the SPA at the Vite dev URL (default
// http://localhost:5173) and proxies /api requests to a locally running
// Scope.Server instance on port 5130 (`dotnet run --project src/Scope.Server`).
// Production builds emit to dist/; scripts/build-frontend.ps1 copies dist/ into
// src/Scope.Server/wwwroot so the next dotnet build embeds the assets.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': 'http://localhost:5130',
    },
  },
});
