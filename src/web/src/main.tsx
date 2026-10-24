import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import './index.css'
import App from './App.tsx'
import { AuthProvider } from './components/AuthProvider.tsx'
import { BrandingProvider } from './components/BrandingProvider.tsx'
import { applyBranding, readCachedBranding } from './lib/branding.ts'

// Before the first render, so a returning visitor's buttons and tab title never
// flash the product's brand while BrandingProvider refetches.
const cachedBranding = readCachedBranding()
if (cachedBranding) {
  applyBranding(cachedBranding)
  document.title = cachedBranding.name
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <BrandingProvider>
        <AuthProvider>
          <App />
        </AuthProvider>
      </BrandingProvider>
    </BrowserRouter>
  </StrictMode>,
)
