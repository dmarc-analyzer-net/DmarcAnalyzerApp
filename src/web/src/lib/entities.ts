export type SyncRunStatus = 'success' | 'failed' | 'running' | 'unknown'

export type Client = {
  id: string
  name: string
  slug: string
  isActive: boolean
  retentionMonths: number
  timezone: string
}

export type Domain = {
  id: string
  name: string
  isActive: boolean
  clientId: string
  clientName: string | null
}

export type MailboxSource = {
  id: string
  name: string
  protocol: 'imap' | 'pop3'
  host: string
  port: number
  useTls: boolean
  username: string
  defaultClientId: string
  defaultClientName: string | null
  isActive: boolean
}

export type MailboxHealth = {
  mailboxSourceId: string
  name: string
  isActive: boolean
  lastSuccessSyncAtUtc: string | null
  lastProcessedUid: number | null
  lastProcessedUidValidity: number | null
  lastRunStatus: SyncRunStatus | null
  lastRunStartedAtUtc: string | null
  lastRunFinishedAtUtc: string | null
  lastRunError: string | null
  lastRunMessagesScanned: number | null
  lastRunAttachmentsProcessed: number | null
  lastRunReportsInserted: number | null
  lastRunReportsSkippedAsDuplicate: number | null
  lastRunParseFailures: number | null
}

export type MailboxSyncRun = {
  id: string
  mailboxSourceId: string
  trigger: string
  status: SyncRunStatus
  startedAtUtc: string
  finishedAtUtc: string | null
  messagesScanned: number
  attachmentsProcessed: number
  reportsInserted: number
  reportsSkippedAsDuplicate: number
  parseFailures: number
  error: string | null
  createdAtUtc: string
}
