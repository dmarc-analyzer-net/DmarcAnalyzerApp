import {
  ArrowDownRight,
  ArrowRight,
  ArrowUpRight,
  BookOpen,
  Calendar,
  ChartColumn,
  Check,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  CircleAlert,
  CircleCheck,
  Clock,
  Copy,
  Database,
  Download,
  Ellipsis,
  ExternalLink,
  Eye,
  EyeOff,
  FileText,
  Funnel,
  Globe,
  Inbox,
  Info,
  KeyRound,
  LayoutDashboard,
  ListFilter,
  LoaderCircle,
  Lock,
  LogOut,
  Mail,
  Pencil,
  Plus,
  RefreshCw,
  Rocket,
  Search,
  Server,
  Settings,
  ShieldCheck,
  Star,
  Trash2,
  TriangleAlert,
  UserRound,
  Users,
  X,
  Zap,
} from 'lucide-react'
import type { ComponentType } from 'react'

/**
 * Thin wrapper over lucide-react addressed by the design system's kebab-case
 * icon names (see the `Icon name="..."` usage in the design bundle). Ships no
 * static SVGs — every glyph resolves to a lucide-react component.
 */

type LucideComponent = ComponentType<{
  size?: number | string
  strokeWidth?: number | string
  className?: string
  'aria-hidden'?: boolean
}>

const ICONS = {
  'arrow-down-right': ArrowDownRight,
  'arrow-right': ArrowRight,
  'arrow-up-right': ArrowUpRight,
  'book-open': BookOpen,
  calendar: Calendar,
  'chart-column': ChartColumn,
  check: Check,
  'chevron-down': ChevronDown,
  'chevron-left': ChevronLeft,
  'chevron-right': ChevronRight,
  'circle-alert': CircleAlert,
  'circle-check': CircleCheck,
  clock: Clock,
  copy: Copy,
  database: Database,
  download: Download,
  ellipsis: Ellipsis,
  'external-link': ExternalLink,
  eye: Eye,
  'eye-off': EyeOff,
  'file-text': FileText,
  funnel: Funnel,
  globe: Globe,
  inbox: Inbox,
  info: Info,
  'key-round': KeyRound,
  'layout-dashboard': LayoutDashboard,
  'list-filter': ListFilter,
  'loader-circle': LoaderCircle,
  lock: Lock,
  'log-out': LogOut,
  mail: Mail,
  pencil: Pencil,
  plus: Plus,
  'refresh-cw': RefreshCw,
  rocket: Rocket,
  search: Search,
  server: Server,
  settings: Settings,
  'shield-check': ShieldCheck,
  star: Star,
  'trash-2': Trash2,
  'triangle-alert': TriangleAlert,
  'user-round': UserRound,
  users: Users,
  x: X,
  zap: Zap,
} satisfies Record<string, LucideComponent>

export type IconName = keyof typeof ICONS

type IconProps = {
  name: IconName
  size?: number
  strokeWidth?: number
  className?: string
}

export function Icon({ name, size = 16, strokeWidth = 2, className }: IconProps) {
  const Glyph = ICONS[name]
  return <Glyph size={size} strokeWidth={strokeWidth} className={className} aria-hidden />
}
