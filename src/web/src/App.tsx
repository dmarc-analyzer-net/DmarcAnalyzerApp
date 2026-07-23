import { Loader2 } from 'lucide-react'
import { Navigate, Route, Routes } from 'react-router-dom'

import { ConsoleLayout } from '@/components/ConsoleLayout'
import { LoginPage } from '@/components/LoginPage'
import { useAuth } from '@/lib/auth-context'
import { isAdmin, isStaff } from '@/lib/authz'
import { ClientsPage } from '@/pages/ClientsPage'
import { DashboardPage } from '@/pages/DashboardPage'
import { DomainDetailPage } from '@/pages/DomainDetailPage'
import { DomainsPage } from '@/pages/DomainsPage'
import { MailboxSourcesPage } from '@/pages/MailboxSourcesPage'
import { UsersPage } from '@/pages/UsersPage'

function App() {
  const { status, user } = useAuth()

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

  // Server-side enforcement is the real guard; these route gates just keep
  // unauthorized roles from landing on pages that would only render 403s.
  const staff = isStaff(user)
  const admin = isAdmin(user)
  const fallback = <Navigate to="/dashboard" replace />

  return (
    <Routes>
      <Route element={<ConsoleLayout />}>
        <Route path="/" element={<Navigate to="/dashboard" replace />} />
        <Route path="/dashboard" element={<DashboardPage />} />
        <Route path="/clients" element={staff ? <ClientsPage /> : fallback} />
        <Route path="/domains" element={<DomainsPage />} />
        <Route path="/domains/:domainId" element={<DomainDetailPage />} />
        <Route path="/mailbox-sources" element={staff ? <MailboxSourcesPage /> : fallback} />
        <Route path="/users" element={admin ? <UsersPage /> : fallback} />
        <Route path="*" element={<Navigate to="/dashboard" replace />} />
      </Route>
    </Routes>
  )
}

export default App
