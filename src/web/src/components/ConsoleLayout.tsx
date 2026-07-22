import { Globe, LayoutDashboard, LogOut, Mail, ShieldCheck } from 'lucide-react'
import { NavLink, Outlet } from 'react-router-dom'

import { BrandLogo } from '@/components/BrandLogo'
import { Button, buttonVariants } from '@/components/ui/button'
import { useAuth } from '@/lib/auth-context'
import { cn } from '@/lib/utils'

const navItems: Array<{ to: string; title: string; icon: typeof ShieldCheck }> = [
  { to: '/dashboard', title: 'Dashboard', icon: LayoutDashboard },
  { to: '/clients', title: 'Clients', icon: ShieldCheck },
  { to: '/domains', title: 'Domains', icon: Globe },
  { to: '/mailbox-sources', title: 'Mailbox Sources', icon: Mail },
]

export function ConsoleLayout() {
  const { user, logout } = useAuth()

  return (
    <div className="mx-auto grid min-h-screen w-full max-w-[1280px] grid-cols-1 gap-6 px-4 py-6 lg:grid-cols-[250px_1fr]">
      <aside className="flex flex-col rounded-lg border bg-card p-4 shadow-panel">
        <BrandLogo className="mb-4 h-auto w-full max-w-[205px]" />
        <nav className="space-y-2">
          {navItems.map((item) => {
            const Icon = item.icon
            return (
              <NavLink
                key={item.to}
                to={item.to}
                className={({ isActive }) =>
                  cn(
                    buttonVariants({ variant: isActive ? 'default' : 'secondary' }),
                    'w-full justify-start',
                  )
                }
              >
                <Icon className="h-4 w-4" />
                {item.title}
              </NavLink>
            )
          })}
        </nav>
        <div className="mt-auto">
          <div className="mt-6 border-t pt-4">
            <p className="truncate text-sm font-medium">{user?.displayName || user?.email}</p>
            <p className="truncate text-xs text-muted-foreground">{user?.email}</p>
            <Button
              variant="outline"
              size="sm"
              className="mt-3 w-full justify-start"
              onClick={() => void logout()}
            >
              <LogOut className="h-4 w-4" />
              Sign out
            </Button>
          </div>
        </div>
      </aside>

      <main className="space-y-4">
        <Outlet />
      </main>
    </div>
  )
}
