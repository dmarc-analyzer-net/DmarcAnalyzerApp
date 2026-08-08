/* eslint-disable react-refresh/only-export-components -- re-exports Radix primitives alongside styled wrappers */
import * as DialogPrimitive from '@radix-ui/react-dialog'
import type * as React from 'react'

import { cn } from '@/lib/utils'

export const Dialog = DialogPrimitive.Root
export const DialogTrigger = DialogPrimitive.Trigger
export const DialogPortal = DialogPrimitive.Portal
export const DialogClose = DialogPrimitive.Close

export function DialogOverlay({
  className,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Overlay>) {
  return (
    <DialogPrimitive.Overlay
      className={cn('fixed inset-0 z-40 bg-[rgba(11,29,24,0.45)] backdrop-blur-[1px]', className)}
      {...props}
    />
  )
}

export function DialogContent({
  className,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Content>) {
  return (
    <DialogPortal>
      <DialogOverlay />
      <DialogPrimitive.Content
        className={cn(
          // The height cap is what makes these usable on a phone: several of the
          // forms are taller than a mobile viewport, and a centred dialog with no
          // max-height simply loses its footer — submit included — off both edges.
          // dvh rather than vh so the collapsing browser chrome doesn't hide it.
          'fixed left-1/2 top-1/2 z-50 max-h-[calc(100dvh-2rem)] w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 -translate-y-1/2 overflow-y-auto rounded-lg border border-border bg-surface-card p-4 shadow-overlay focus:outline-none sm:p-6',
          className,
        )}
        {...props}
      />
    </DialogPortal>
  )
}

export function DialogHeader({ className, ...props }: React.ComponentProps<'div'>) {
  return <div className={cn('mb-4', className)} {...props} />
}

export function DialogTitle({
  className,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Title>) {
  return (
    <DialogPrimitive.Title
      className={cn('font-display text-lg font-bold tracking-tight text-body', className)}
      {...props}
    />
  )
}

export function DialogDescription({
  className,
  ...props
}: React.ComponentProps<typeof DialogPrimitive.Description>) {
  return (
    <DialogPrimitive.Description className={cn('text-sm text-secondary', className)} {...props} />
  )
}
