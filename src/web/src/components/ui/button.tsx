import { Slot } from '@radix-ui/react-slot'
import { cva, type VariantProps } from 'class-variance-authority'
import type * as React from 'react'

import { Icon, type IconName } from '@/components/ui/icon'
import { cn } from '@/lib/utils'

/**
 * Design-system button. Solid teal `primary`, bordered `secondary`/`outline`,
 * and `ghost`; hover always DARKENS (never lightens) and focus draws the 3px
 * teal ring. `icon` renders a leading lucide glyph by kebab name.
 */
const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-md font-body font-semibold transition-colors duration-[120ms] ease-out focus-visible:outline-none focus-visible:shadow-[var(--focus-ring)] disabled:pointer-events-none disabled:opacity-50',
  {
    variants: {
      variant: {
        primary: 'bg-brand text-white hover:bg-brand-hover',
        secondary: 'border border-border bg-surface-card text-body hover:bg-gray-100',
        outline: 'border border-border bg-surface-card text-body hover:bg-gray-100',
        ghost: 'text-body hover:bg-gray-100',
        danger: 'bg-red-600 text-white hover:bg-red-800',
        mint: 'bg-mint-400 text-ink-900 hover:bg-mint-300',
        inkOutline: 'border border-white/20 text-white/90 hover:bg-white/[0.06]',
      },
      size: {
        sm: 'h-8 px-3 text-sm',
        md: 'h-9 px-4 text-base',
        lg: 'h-[42px] px-[22px] text-md',
      },
    },
    defaultVariants: {
      variant: 'primary',
      size: 'md',
    },
  },
)

export type ButtonProps = React.ButtonHTMLAttributes<HTMLButtonElement> &
  VariantProps<typeof buttonVariants> & {
    asChild?: boolean
    /** Leading icon rendered by kebab name (ignored when `asChild`). */
    icon?: IconName
  }

export function Button({
  className,
  variant,
  size,
  asChild = false,
  icon,
  children,
  ...props
}: ButtonProps) {
  const Comp = asChild ? Slot : 'button'
  return (
    <Comp className={cn(buttonVariants({ variant, size }), className)} {...props}>
      {asChild ? (
        children
      ) : (
        <>
          {icon ? <Icon name={icon} size={size === 'sm' ? 14 : 16} /> : null}
          {children}
        </>
      )}
    </Comp>
  )
}
