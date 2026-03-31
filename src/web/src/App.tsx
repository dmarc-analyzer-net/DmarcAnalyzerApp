import { useEffect, useState } from 'react'
import './App.css'

function App() {
  const [status, setStatus] = useState<string>('Loading API status...')

  useEffect(() => {
    const fetchStatus = async () => {
      try {
        const response = await fetch('/api/v1/system/status')
        if (!response.ok) {
          throw new Error(`Request failed (${response.status})`)
        }
        const payload = (await response.json()) as {
          service: string
          mode: string
          timestampUtc: string
        }
        setStatus(`${payload.service} (${payload.mode}) at ${payload.timestampUtc}`)
      } catch (error) {
        if (error instanceof Error) {
          setStatus(`API unavailable: ${error.message}`)
        } else {
          setStatus('API unavailable')
        }
      }
    }

    fetchStatus()
  }, [])

  return (
    <main className="app-shell">
      <header>
        <img
          className="brand-logo"
          src="/dmarc-analyzer-net-logo.svg"
          alt="DMARC Analyzer .NET"
        />
        <p>Single-image ASP.NET + React baseline is ready.</p>
      </header>

      <section className="status-card">
        <h2>API Connectivity</h2>
        <p>{status}</p>
      </section>
    </main>
  )
}

export default App
