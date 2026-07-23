import type { ReactNode } from 'react'
import { NavLink, Outlet } from 'react-router-dom'

import { BrandLogo } from '@/components/BrandLogo'
import { Icon, type IconName } from '@/components/ui/icon'
import type { AuthUser } from '@/lib/auth-context'
import { useAuth } from '@/lib/auth-context'
import { isAdmin, isStaff } from '@/lib/authz'
import { cn } from '@/lib/utils'

type NavItem = {
  to: string
  label: string
  icon: IconName
  /** Least privileged role that gets the item; defaults to every signed-in user. */
  visibleTo?: (user: AuthUser | null) => boolean
}

const primaryNav: NavItem[] = [
  { to: '/dashboard', label: 'Dashboard', icon: 'layout-dashboard' },
  { to: '/domains', label: 'Domains', icon: 'globe' },
]

const manageNav: NavItem[] = [
  { to: '/clients', label: 'Clients', icon: 'shield-check', visibleTo: isStaff },
  { to: '/users', label: 'Users', icon: 'users', visibleTo: isAdmin },
  { to: '/mailbox-sources', label: 'Mailbox sources', icon: 'mail', visibleTo: isStaff },
]

function NavItemLink({ item }: { item: NavItem }) {
  return (
    <NavLink
      to={item.to}
      className={({ isActive }) =>
        cn(
          'flex items-center gap-2.5 rounded-md px-3 py-2 font-body text-base transition-colors duration-[120ms] ease-out focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none',
          isActive
            ? 'bg-brand-subtle font-semibold text-teal-800'
            : 'font-medium text-gray-600 hover:bg-gray-100',
        )
      }
    >
      <Icon name={item.icon} size={16} />
      <span className="flex-1">{item.label}</span>
    </NavLink>
  )
}

function SectionLabel({ children }: { children: ReactNode }) {
  return (
    <div className="mb-1.5 px-3 text-xs font-semibold tracking-wide text-faint uppercase">
      {children}
    </div>
  )
}

export function ConsoleLayout() {
  const { user, logout } = useAuth()
  const visibleManage = manageNav.filter((item) => item.visibleTo?.(user) ?? true)

  return (
    <div className="flex min-h-screen items-stretch bg-surface-page">
      <aside className="sticky top-0 flex h-screen w-[var(--sidebar-w)] shrink-0 flex-col gap-0.5 border-r border-border bg-surface-card px-3 py-[18px]">
        <div className="mb-4 px-3">
          <BrandLogo className="h-[30px] w-auto" />
        </div>

        <SectionLabel>Overview</SectionLabel>
        {primaryNav.map((item) => (
          <NavItemLink key={item.to} item={item} />
        ))}

        {visibleManage.length > 0 && (
          <>
            <div className="mt-4">
              <SectionLabel>Manage</SectionLabel>
            </div>
            {visibleManage.map((item) => (
              <NavItemLink key={item.to} item={item} />
            ))}
          </>
        )}

        <div className="mt-auto border-t border-gray-100 px-3 pt-3">
          <p className="truncate text-sm font-semibold text-body">
            {user?.displayName || user?.email}
          </p>
          <p className="truncate text-xs text-secondary">{user?.email}</p>
          <button
            type="button"
            onClick={() => void logout()}
            className="mt-3 flex w-full items-center gap-2.5 rounded-md px-3 py-2 font-body text-base font-medium text-gray-600 transition-colors duration-[120ms] ease-out hover:bg-gray-100 focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none"
          >
            <Icon name="log-out" size={16} />
            <span className="flex-1 text-left">Sign out</span>
          </button>
        </div>
      </aside>

      <main className="min-w-0 flex-1 basis-0 px-8 py-[26px]">
        <div className="max-w-[1040px]">
          <Outlet />
        </div>
      </main>
    </div>
  )
}
