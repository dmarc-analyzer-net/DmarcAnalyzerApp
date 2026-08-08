import { useEffect, useRef, useState, type ReactNode } from 'react'
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
  { to: '/threats', label: 'Threats', icon: 'triangle-alert' },
  { to: '/alerts', label: 'Alerts', icon: 'circle-alert' },
]

const manageNav: NavItem[] = [
  { to: '/clients', label: 'Clients', icon: 'shield-check', visibleTo: isStaff },
  { to: '/users', label: 'Users', icon: 'users', visibleTo: isAdmin },
  { to: '/mailbox-sources', label: 'Mailbox sources', icon: 'mail', visibleTo: isStaff },
  { to: '/notifications', label: 'Notifications', icon: 'inbox', visibleTo: isStaff },
  { to: '/audit', label: 'Audit trail', icon: 'file-text', visibleTo: isAdmin },
  { to: '/backup', label: 'Backup and recovery', icon: 'cloud-upload', visibleTo: isAdmin },
]

function NavItemLink({ item, onNavigate }: { item: NavItem; onNavigate: () => void }) {
  return (
    <NavLink
      to={item.to}
      onClick={onNavigate}
      className={({ isActive }) =>
        cn(
          // py-2.5 below lg keeps the row near the 44px touch target; the desktop
          // sidebar goes back to the tighter py-2 rhythm.
          'flex items-center gap-2.5 rounded-md px-3 py-2.5 font-body text-base transition-colors duration-[120ms] ease-out focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none lg:py-2',
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

  // Below lg the sidebar is an off-canvas drawer. At lg and up this state is
  // inert — the sidebar is a static column and the trigger is not rendered.
  const [navOpen, setNavOpen] = useState(false)
  const openButtonRef = useRef<HTMLButtonElement>(null)
  const closeButtonRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    if (!navOpen) return

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setNavOpen(false)
    }
    document.addEventListener('keydown', onKeyDown)

    // Without this the page behind the drawer scrolls under the finger.
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'

    closeButtonRef.current?.focus()

    return () => {
      document.removeEventListener('keydown', onKeyDown)
      document.body.style.overflow = previousOverflow
    }
  }, [navOpen])

  // Also the drawer's exit on navigation: the links stay mounted when it hides,
  // so without moving focus back the caret would sit on an invisible element.
  const closeNav = () => {
    setNavOpen(false)
    openButtonRef.current?.focus()
  }

  return (
    <div className="flex min-h-screen items-stretch bg-surface-page">
      {navOpen && (
        <button
          type="button"
          aria-label="Close navigation"
          onClick={closeNav}
          className="fixed inset-0 z-40 bg-ink-900/40 lg:hidden"
        />
      )}

      <aside
        id="console-nav"
        className={cn(
          // Wider as a drawer than as a sidebar: 230px has no room left for the
          // close button once the lockup is in, and there is no reason to be
          // narrow when the space is borrowed from a backdrop rather than content.
          'fixed inset-y-0 left-0 z-50 flex h-screen w-[280px] shrink-0 flex-col gap-0.5 overflow-y-auto border-r border-border bg-surface-card px-3 py-[18px] transition-transform duration-200 ease-out lg:w-[var(--sidebar-w)]',
          // At lg the drawer machinery switches off: back to a sticky in-flow
          // column that is always visible and never animates.
          'lg:sticky lg:top-0 lg:z-auto lg:visible lg:translate-x-0 lg:transition-none',
          // `invisible` and not just the off-screen transform: a translated
          // drawer still takes focus, so tabbing would walk an unseen menu.
          navOpen ? 'translate-x-0' : 'invisible -translate-x-full',
        )}
      >
        <div className="mb-4 flex items-center justify-between px-3">
          <BrandLogo height={30} />
          <button
            ref={closeButtonRef}
            type="button"
            onClick={closeNav}
            aria-label="Close navigation"
            className="flex h-11 w-11 shrink-0 items-center justify-center rounded-md text-gray-600 transition-colors duration-[120ms] ease-out hover:bg-gray-100 focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none lg:hidden"
          >
            <Icon name="x" size={20} />
          </button>
        </div>

        <SectionLabel>Overview</SectionLabel>
        {primaryNav.map((item) => (
          <NavItemLink key={item.to} item={item} onNavigate={closeNav} />
        ))}

        {visibleManage.length > 0 && (
          <>
            <div className="mt-4">
              <SectionLabel>Manage</SectionLabel>
            </div>
            {visibleManage.map((item) => (
              <NavItemLink key={item.to} item={item} onNavigate={closeNav} />
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
            className="mt-3 flex w-full items-center gap-2.5 rounded-md px-3 py-2.5 font-body text-base font-medium text-gray-600 transition-colors duration-[120ms] ease-out hover:bg-gray-100 focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none lg:py-2"
          >
            <Icon name="log-out" size={16} />
            <span className="flex-1 text-left">Sign out</span>
          </button>
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 basis-0 flex-col">
        <header className="sticky top-0 z-30 flex items-center gap-2 border-b border-border bg-surface-card px-2 py-1.5 lg:hidden">
          <button
            ref={openButtonRef}
            type="button"
            onClick={() => setNavOpen(true)}
            aria-label="Open navigation"
            aria-expanded={navOpen}
            aria-controls="console-nav"
            className="flex h-11 w-11 items-center justify-center rounded-md text-gray-600 transition-colors duration-[120ms] ease-out hover:bg-gray-100 focus-visible:shadow-[var(--focus-ring)] focus-visible:outline-none"
          >
            <Icon name="menu" size={20} />
          </button>
          <BrandLogo height={24} />
        </header>

        <main className="min-w-0 px-4 py-5 sm:px-6 lg:px-8 lg:py-[26px]">
          <div className="max-w-[1040px]">
            <Outlet />
          </div>
        </main>
      </div>
    </div>
  )
}
