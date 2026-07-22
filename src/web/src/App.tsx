import { Loader2 } from 'lucide-react'
import { Navigate, Route, Routes } from 'react-router-dom'

import { ConsoleLayout } from '@/components/ConsoleLayout'
import { LoginPage } from '@/components/LoginPage'
import { useAuth } from '@/lib/auth-context'
import { ClientsPage } from '@/pages/ClientsPage'
import { DashboardPage } from '@/pages/DashboardPage'
import { DomainDetailPage } from '@/pages/DomainDetailPage'
import { DomainsPage } from '@/pages/DomainsPage'
import { MailboxSourcesPage } from '@/pages/MailboxSourcesPage'

function App() {
  const { status } = useAuth()

  if (status === 'loading') {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" aria-label="Loading" />
      </div>
    )
  }

  // The login page renders in place without redirecting, so the requested URL is
  // preserved and the router lands on it once authentication succeeds.
  if (status === 'unauthenticated') {
    return <LoginPage />
  }

  return (
    <Routes>
      <Route element={<ConsoleLayout />}>
        <Route path="/" element={<Navigate to="/dashboard" replace />} />
        <Route path="/dashboard" element={<DashboardPage />} />
        <Route path="/clients" element={<ClientsPage />} />
        <Route path="/domains" element={<DomainsPage />} />
        <Route path="/domains/:domainId" element={<DomainDetailPage />} />
        <Route path="/mailbox-sources" element={<MailboxSourcesPage />} />
        <Route path="*" element={<Navigate to="/dashboard" replace />} />
      </Route>
    </Routes>
  )
}

export default App
