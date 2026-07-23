import type { AuthUser } from '@/lib/auth-context'

/**
 * Role-based authorization helpers. The server enforces permissions; these
 * only decide what the UI shows. Unknown or missing roles are treated as
 * least privilege (viewer-level), so a new server-side role can never
 * accidentally unlock admin UI.
 */

/** Full access: config mutations and user management. */
export function isAdmin(user: AuthUser | null | undefined): boolean {
  return user?.role === 'agency_admin'
}

/** Agency staff (admin or analyst): sees all clients and operational pages. */
export function isStaff(user: AuthUser | null | undefined): boolean {
  return user?.role === 'agency_admin' || user?.role === 'agency_analyst'
}
