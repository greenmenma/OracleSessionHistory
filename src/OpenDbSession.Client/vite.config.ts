import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Aspire から起動した場合は Server の URL が環境変数で渡される
const apiTarget =
  process.env.services__server__https__0 ||
  process.env.services__server__http__0 ||
  process.env.SERVER_HTTPS ||
  process.env.SERVER_HTTP ||
  'http://localhost:5292'

export default defineConfig({
  plugins: [react()],
  server: {
    port: process.env.PORT ? Number(process.env.PORT) : 5173,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: false },
    },
  },
  build: {
    // 本番配置時は Server の wwwroot に出力する
    outDir: '../OpenDbSession.Server/wwwroot',
    emptyOutDir: true,
  },
})
