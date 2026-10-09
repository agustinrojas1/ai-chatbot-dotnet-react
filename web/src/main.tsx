import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { isAdminPath } from './lib/route.ts'
import AdminPage from './pages/AdminPage.tsx'

const rootElement = document.getElementById('root')
if (!rootElement) {
  throw new Error('Root element #root not found in index.html')
}

createRoot(rootElement).render(
  <StrictMode>
    {isAdminPath(window.location.pathname) ? <AdminPage /> : <App />}
  </StrictMode>,
)
