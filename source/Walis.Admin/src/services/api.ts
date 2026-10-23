import type {
  ActionCatalog,
  ActionDefinition,
  ActionPackList,
  ActionPreview,
  ActionStep,
  AiAnswer,
  AiProvider,
  AiProviderInput,
  AiRequestLogEntry,
  AuditEntry,
  AuditFilter,
  AuditPage,
  ApiKey,
  AuthStatus,
  BackupFilesPayload,
  BackupPayload,
  BackupProvider,
  BackupTarget,
  BuildArtifact,
  Build,
  BuildFilter,
  BuildPage,
  DashboardSummary,
  EnvironmentVariable,
  EnvironmentVariableInput,
  Job,
  JenkinsImportResult,
  JobFilter,
  JobPage,
  ScheduleTimeline,
  JobInput,
  LocalizationPayload,
  NotificationChannel,
  OtpEnrollment,
  NotificationLogEntry,
  PluginManifest,
  PluginSummary,
  Runner,
  RunnerInput,
  SecretsStatus,
  SecurityCenterPayload,
  SettingsPayload,
  SignInResult,
  RestoreResult,
  WalisSettings,
  WalisUser
} from '../types'

function resolveApiOrigin(value: string | undefined): string {
  const candidate = value?.trim()

  if (!candidate) {
    return ''
  }

  let parsed: URL

  try {
    parsed = new URL(candidate)
  } catch {
    throw new Error('VITE_API_URL must be an absolute HTTP or HTTPS origin.')
  }

  if (
    !['http:', 'https:'].includes(parsed.protocol) ||
    parsed.username.length > 0 ||
    parsed.password.length > 0 ||
    parsed.pathname !== '/' ||
    parsed.search.length > 0 ||
    parsed.hash.length > 0 ||
    (window.location.protocol === 'https:' && parsed.protocol !== 'https:')
  ) {
    throw new Error(
      'VITE_API_URL must be a non-downgrading HTTP(S) origin without credentials, a path, query, or fragment.'
    )
  }

  return parsed.origin === window.location.origin ? '' : parsed.origin
}

const origin = resolveApiOrigin(import.meta.env.VITE_API_URL)
let onUnauthorized: (() => void) | null = null
let onOtpSetupRequired: (() => void) | null = null

function cookie(name: string): string | null {
  const prefix = `${encodeURIComponent(name)}=`
  const item = document.cookie.split(';').map(value => value.trim()).find(value => value.startsWith(prefix))

  if (!item) {
    return null
  }

  try {
    return decodeURIComponent(item.slice(prefix.length))
  } catch {
    // A malformed cookie must fail closed instead of crashing every mutating request.
    return null
  }
}

/** Encode untrusted route/API identifiers as exactly one URL path segment. */
function segment(value: string): string {
  return encodeURIComponent(value)
}

/**
 * Asks the signed-in administrator for the current password when the server answers 403 with code reauth_required
 * (creating an API key, resetting someone else's password or one-time password, granting admin, turning sign-in or
 * auditing off, lifting a lockout). Resolves to null when the person cancels. The value is only placed on the retried
 * request's X-Walis-Reauth header and never stored.
 */
let reauthPrompt: ((message: string) => Promise<string | null>) | null = null

export function onReauthRequired(handler: (message: string) => Promise<string | null>): void {
  reauthPrompt = handler
}

/** Tells the application to fall back to the sign-in screen on 401. */
export function onUnauthorizedResponse(handler: () => void): void {
  onUnauthorized = handler
}

/** Tells the application when the backend requires one-time-password setup first (the setting may be turned on after sign-in). */
export function onOtpSetupRequiredResponse(handler: () => void): void {
  onOtpSetupRequired = handler
}

/** A validation error returned by the API; the fields map to the form field names. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly errors: Record<string, string[]> = {},
    /** When two-factor verification fails, the backend attaches a retryable challenge code. */
    readonly challenge?: string
  ) {
    super(message)
    this.name = 'ApiError'
  }

  fieldError(field: string): string | undefined {
    return this.errors[field]?.[0]
  }
}

async function request<T>(path: string, options?: RequestInit, reauthAttempt = 0): Promise<T> {
  let response: Response
  const method = (options?.method ?? 'GET').toUpperCase()
  const csrf = !['GET', 'HEAD', 'OPTIONS'].includes(method) ? cookie('walis_csrf') : null
  const headers = new Headers(options?.headers)

  headers.set('Accept', 'application/json')

  if (options?.body !== undefined && options.body !== null && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  if (csrf) {
    // Internal callers cannot accidentally replace the double-submit token.
    headers.set('X-Walis-CSRF', csrf)
  }

  try {
    response = await fetch(`${origin}${path}`, {
      ...options,
      credentials: 'include',
      redirect: 'error',
      cache: 'no-store',
      referrerPolicy: 'no-referrer',
      headers
    })
  } catch {
    throw new ApiError('無法連線到 Walis API，請確認服務是否已啟動。', 0)
  }

  if (!response.ok) {
    if (response.status === 401) {
      onUnauthorized?.()
    }

    const payload = (await response.json().catch(() => null)) as
      | {
          message?: string
          title?: string
          errors?: Record<string, string[]>
          challenge?: string
          otpSetupRequired?: boolean
          code?: string
        }
      | null

    if (response.status === 403 && payload?.otpSetupRequired) {
      onOtpSetupRequired?.()
    }

    // Step-up: ask for the current password and send the same request again (at most three tries, then the error stands).
    if (response.status === 403 && payload?.code === 'reauth_required' && reauthPrompt && reauthAttempt < 3) {
      const password = await reauthPrompt(payload.message ?? '')
      if (password) {
        const retryHeaders = new Headers(options?.headers)
        retryHeaders.set('X-Walis-Reauth', password)
        return request<T>(path, { ...options, headers: retryHeaders }, reauthAttempt + 1)
      }
    }

    const validationMessage = payload?.errors ? Object.values(payload.errors).flat().join(' ') : undefined

    throw new ApiError(
      payload?.message ?? validationMessage ?? payload?.title ?? `請求失敗（${response.status}）`,
      response.status,
      payload?.errors ?? {},
      payload?.challenge
    )
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}

function auditQuery(filter: AuditFilter): URLSearchParams {
  const parameters = new URLSearchParams()
  if (filter.actor) parameters.set('actor', filter.actor)
  if (filter.entityType) parameters.set('entityType', filter.entityType)
  if (filter.entityId) parameters.set('entityId', filter.entityId)
  if (filter.action) parameters.set('action', filter.action)
  if (filter.outcome) parameters.set('outcome', filter.outcome)
  if (filter.from) parameters.set('from', filter.from)
  if (filter.to) parameters.set('to', filter.to)
  if (filter.page) parameters.set('page', String(filter.page))
  if (filter.pageSize) parameters.set('pageSize', String(filter.pageSize))
  return parameters
}

function query(filter: BuildFilter): string {
  const parameters = new URLSearchParams()

  if (filter.status) parameters.set('status', filter.status)
  if (filter.runner) parameters.set('runner', filter.runner)
  if (filter.jobId) parameters.set('jobId', filter.jobId)
  if (filter.search) parameters.set('search', filter.search)
  parameters.set('limit', String(filter.limit ?? 50))

  return parameters.toString()
}

export const api = {
  health: () => request<{ status: string }>('/health'),

  dashboard: () => request<DashboardSummary>('/api/dashboard'),
  scheduleTimeline: (date: string | null, days: number) =>
    request<ScheduleTimeline>(`/api/schedules/timeline?days=${days}${date ? `&date=${encodeURIComponent(date)}` : ''}`),

  jobs: () => request<Job[]>('/api/jobs/'),
  importJenkins: (content: string, name: string | null) =>
    request<JenkinsImportResult>('/api/jobs/import/jenkins', { method: 'POST', body: JSON.stringify({ content, name }) }),
  jobPage: (filter: JobFilter = {}) => {
    const parameters = new URLSearchParams()
    if (filter.search) parameters.set('search', filter.search)
    if (filter.status) parameters.set('status', filter.status)
    if (filter.trigger) parameters.set('trigger', filter.trigger)
    if (filter.project) parameters.set('project', filter.project)
    if (filter.sort) parameters.set('sort', filter.sort)
    if (filter.order) parameters.set('order', filter.order)
    parameters.set('page', String(filter.page ?? 1))
    parameters.set('pageSize', String(filter.pageSize ?? 25))
    return request<JobPage>(`/api/jobs/page?${parameters.toString()}`)
  },
  job: (id: string) => request<Job>(`/api/jobs/${segment(id)}`),
  jobBuilds: (id: string, limit = 20) => request<Build[]>(`/api/jobs/${segment(id)}/builds?limit=${limit}`),
  createJob: (payload: JobInput) => request<Job>('/api/jobs/', { method: 'POST', body: JSON.stringify(payload) }),
  updateJob: (id: string, payload: JobInput) =>
    request<Job>(`/api/jobs/${segment(id)}`, { method: 'PUT', body: JSON.stringify(payload) }),
  setJobEnabled: (id: string, enabled: boolean) =>
    request<Job>(`/api/jobs/${segment(id)}/enabled`, { method: 'PATCH', body: JSON.stringify({ enabled }) }),
  deleteJob: (id: string) => request<void>(`/api/jobs/${segment(id)}`, { method: 'DELETE' }),
  runJob: (id: string, triggeredBy = 'manual', parameters?: Record<string, string>) =>
    request<Build>(`/api/jobs/${segment(id)}/builds`, {
      method: 'POST',
      body: JSON.stringify(parameters ? { triggeredBy, parameters } : { triggeredBy })
    }),

  builds: (filter: BuildFilter = {}) => request<Build[]>(`/api/builds/?${query(filter)}`),
  buildPage: (filter: BuildFilter = {}) => {
    const parameters = new URLSearchParams()
    if (filter.status) parameters.set('status', filter.status)
    if (filter.runner) parameters.set('runner', filter.runner)
    if (filter.jobId) parameters.set('jobId', filter.jobId)
    if (filter.search) parameters.set('search', filter.search)
    if (filter.page) parameters.set('page', String(filter.page))
    if (filter.pageSize) parameters.set('pageSize', String(filter.pageSize))

    return request<BuildPage>(`/api/builds/page?${parameters.toString()}`)
  },
  buildArtifacts: (id: string) => request<BuildArtifact[]>(`/api/builds/${segment(id)}/artifacts`),
  artifactUrl: (buildId: string, artifactId: string) =>
    `${origin}/api/builds/${segment(buildId)}/artifacts/${segment(artifactId)}`,
  build: (id: string) => request<Build>(`/api/builds/${segment(id)}`),
  cancelBuild: (id: string) => request<Build>(`/api/builds/${segment(id)}/cancel`, { method: 'POST' }),

  runners: () => request<Runner[]>('/api/runners/'),
  createRunner: (payload: RunnerInput) =>
    request<Runner>('/api/runners/', { method: 'POST', body: JSON.stringify(payload) }),
  updateRunner: (id: string, payload: RunnerInput) =>
    request<Runner>(`/api/runners/${segment(id)}`, { method: 'PUT', body: JSON.stringify(payload) }),
  setRunnerPaused: (id: string, paused: boolean) =>
    request<Runner>(`/api/runners/${segment(id)}/paused`, { method: 'PATCH', body: JSON.stringify({ paused }) }),
  deleteRunner: (id: string) =>
    request<{ disabledKeyCount: number }>(`/api/runners/${segment(id)}`, { method: 'DELETE' }),

  environmentVariables: (reveal = false) =>
    request<EnvironmentVariable[]>(`/api/environment-variables/?reveal=${reveal}`),
  createEnvironmentVariable: (payload: EnvironmentVariableInput) =>
    request<EnvironmentVariable>('/api/environment-variables/', { method: 'POST', body: JSON.stringify(payload) }),
  updateEnvironmentVariable: (id: string, payload: EnvironmentVariableInput) =>
    request<EnvironmentVariable>(`/api/environment-variables/${segment(id)}`, { method: 'PUT', body: JSON.stringify(payload) }),
  deleteEnvironmentVariable: (id: string) =>
    request<void>(`/api/environment-variables/${segment(id)}`, { method: 'DELETE' }),

  notificationChannels: () => request<NotificationChannel[]>('/api/notifications/channels'),
  updateNotificationChannel: (id: string, payload: Omit<NotificationChannel, 'id' | 'updatedAt'>) =>
    request<NotificationChannel>(`/api/notifications/channels/${segment(id)}`, {
      method: 'PUT',
      body: JSON.stringify(payload)
    }),
  testNotificationChannel: (id: string) =>
    request<NotificationLogEntry>(`/api/notifications/channels/${segment(id)}/test`, { method: 'POST' }),
  notificationLog: (limit = 30) => request<NotificationLogEntry[]>(`/api/notifications/log?limit=${limit}`),

  settings: () => request<SettingsPayload>('/api/settings/'),
  settingsSecrets: () => request<SecretsStatus>('/api/settings/secrets'),
  resetUndecryptableSecrets: () =>
    request<SecretsStatus>('/api/settings/secrets/reset-undecryptable', { method: 'POST' }),

  plugins: () => request<PluginSummary[]>('/api/plugins/'),

  generatePlugin: (requirement: string, providerId: string | null = null) =>
    request<{
      ok: boolean
      manifest?: PluginManifest
      code?: string
      warnings?: string[]
      message?: string
      raw?: string
    }>('/api/plugins/generate', {
      method: 'POST',
      body: JSON.stringify({ requirement, providerId })
    }),

  installPlugin: (manifest: unknown, code: string, isTrusted: boolean) =>
    request<PluginSummary>('/api/plugins/', {
      method: 'POST',
      body: JSON.stringify({ manifest, code, isTrusted })
    }),

  previewPlugin: (manifest: unknown, code: string, isTrusted = false, inputs: Record<string, string> = {}) =>
    request<{ ok: boolean; script: string; message: string }>('/api/plugins/preview', {
      method: 'POST',
      body: JSON.stringify({ manifest, code, inputs, isTrusted })
    }),

  setPluginDisabled: (pluginId: string, isDisabled: boolean) =>
    request<PluginSummary>(`/api/plugins/${segment(pluginId)}/disabled`, {
      method: 'POST',
      body: JSON.stringify({ isDisabled })
    }),

  setPluginTrusted: (pluginId: string, isTrusted: boolean) =>
    request<PluginSummary>(`/api/plugins/${segment(pluginId)}/trusted`, {
      method: 'POST',
      body: JSON.stringify({ isTrusted })
    }),

  uninstallPlugin: (pluginId: string) => request<void>(`/api/plugins/${segment(pluginId)}`, { method: 'DELETE' }),

  securityCenter: () => request<SecurityCenterPayload>('/api/security-center'),
  unlockSignInLockout: (key: string) =>
    request<void>(`/api/security-center/lockouts?key=${encodeURIComponent(key)}`, { method: 'DELETE' }),

  exportJobs: () => request<unknown>('/api/portability/export'),

  importJobs: (bundle: unknown, overwrite: boolean) =>
    request<{ created: number; updated: number; skipped: number; actions: number }>('/api/portability/import', {
      method: 'POST',
      body: JSON.stringify({ bundle, overwrite })
    }),

  apiKeys: () => request<ApiKey[]>('/api/api-keys/'),

  createApiKey: (payload: {
    name: string
    role: string
    scope: 'mcp' | 'api'
    expiresAt: string | null
    runnerName: string | null
    allowedAddresses: string | null
  }) =>
    request<{ key: ApiKey; token: string }>('/api/api-keys/', {
      method: 'POST',
      body: JSON.stringify(payload)
    }),

  setApiKeyDisabled: (id: string, isDisabled: boolean) =>
    request<ApiKey>(`/api/api-keys/${segment(id)}/disabled`, {
      method: 'POST',
      body: JSON.stringify({ isDisabled })
    }),

  deleteApiKey: (id: string) => request<void>(`/api/api-keys/${segment(id)}`, { method: 'DELETE' }),

  backup: () => request<BackupPayload>('/api/backup/'),

  runBackup: () => request<void>('/api/backup/run', { method: 'POST' }),

  backupFiles: () => request<BackupFilesPayload>('/api/backup/files'),

  restoreBackup: (payload: {
    fileName: string
    passphrase?: string
    includeArtifacts: boolean
  }) => request<RestoreResult>('/api/backup/restore', { method: 'POST', body: JSON.stringify(payload) }),

  backupOAuthUrl: (provider: BackupProvider, clientId: string) =>
    request<{ url: string; redirectUri: string }>('/api/backup/oauth/authorize-url', {
      method: 'POST',
      body: JSON.stringify({ provider, clientId })
    }),
  backupOAuthExchange: (payload: {
    provider: BackupProvider
    clientId: string | null
    clientSecret: string | null
    code: string
    redirectUri: string | null
    targetId: string | null
  }) =>
    request<{ ok: boolean; refreshToken: string | null; message: string }>('/api/backup/oauth/exchange', {
      method: 'POST',
      body: JSON.stringify(payload)
    }),
  testBackupTarget: (target: BackupTarget) =>
    request<{ ok: boolean; message: string }>('/api/backup/test', {
      method: 'POST',
      body: JSON.stringify(target)
    }),
  saveSettings: (payload: WalisSettings) =>
    request<WalisSettings>('/api/settings/', { method: 'PUT', body: JSON.stringify(payload) }),

  audit: (limit = 80) => request<AuditEntry[]>(`/api/audit?limit=${limit}`),
  auditPage: (filter: AuditFilter = {}) =>
    request<AuditPage>(`/api/audit/page?${auditQuery(filter).toString()}`),
  /** The CSV export is a plain download: the browser follows the link with its session cookie. */
  auditExportUrl: (filter: AuditFilter = {}) => `${origin}/api/audit/export.csv?${auditQuery(filter).toString()}`,

  authStatus: () => request<AuthStatus>('/api/auth/status'),
  signIn: (username: string, password: string) =>
    request<SignInResult>('/api/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) }),
  verifyOtp: (challenge: string, code: string) =>
    request<SignInResult>('/api/auth/login/otp', { method: 'POST', body: JSON.stringify({ challenge, code }) }),
  setupAccount: (
    username: string,
    displayName: string,
    password: string,
    bootstrapToken = '',
    allowUnisolatedBuilds: boolean | null = null
  ) =>
    request<SignInResult>('/api/auth/setup', {
      method: 'POST',
      headers: bootstrapToken.trim() ? { 'X-Walis-Bootstrap-Token': bootstrapToken.trim() } : {},
      // Null leaves the isolation setting alone; the setup screen only sends an answer when it showed the explanation.
      body: JSON.stringify({ username, displayName, password, allowUnisolatedBuilds })
    }),
  signOut: () => request<void>('/api/auth/logout', { method: 'POST' }),
  /** Revokes every session of the signed-in account, this one included. */
  signOutEverywhere: () => request<{ revoked: number }>('/api/auth/logout-all', { method: 'POST' }),
  changePassword: (currentPassword: string, newPassword: string) =>
    request<void>('/api/auth/password', {
      method: 'POST',
      body: JSON.stringify({ currentPassword, newPassword })
    }),

  startOtpEnrollment: (currentPassword: string) =>
    request<OtpEnrollment>('/api/auth/otp/setup', { method: 'POST', body: JSON.stringify({ currentPassword }) }),
  enableOtp: (code: string, currentPassword: string) =>
    request<{ recoveryCodes: string[] }>('/api/auth/otp/enable', {
      method: 'POST',
      body: JSON.stringify({ code, currentPassword })
    }),
  disableOtp: (currentPassword: string) =>
    request<void>('/api/auth/otp/disable', { method: 'POST', body: JSON.stringify({ currentPassword }) }),
  regenerateRecoveryCodes: (currentPassword: string) =>
    request<{ recoveryCodes: string[] }>('/api/auth/otp/recovery', {
      method: 'POST',
      body: JSON.stringify({ currentPassword })
    }),

  accounts: () => request<WalisUser[]>('/api/accounts'),
  createAccount: (username: string, displayName: string, password: string, role: string) =>
    request<WalisUser>('/api/accounts', {
      method: 'POST',
      body: JSON.stringify({ username, displayName, password, role })
    }),
  updateAccount: (id: string, displayName: string, role: string, isDisabled: boolean) =>
    request<WalisUser>(`/api/accounts/${segment(id)}`, {
      method: 'PUT',
      body: JSON.stringify({ displayName, role, isDisabled })
    }),
  resetAccountPassword: (id: string, newPassword: string) =>
    request<void>(`/api/accounts/${segment(id)}/password`, { method: 'POST', body: JSON.stringify({ newPassword }) }),
  resetAccountOtp: (id: string) => request<void>(`/api/accounts/${segment(id)}/otp/reset`, { method: 'POST' }),
  deleteAccount: (id: string) => request<void>(`/api/accounts/${segment(id)}`, { method: 'DELETE' }),

  aiProviders: () => request<AiProvider[]>('/api/ai/providers'),
  updateAiProvider: (id: string, payload: AiProviderInput) =>
    request<AiProvider>(`/api/ai/providers/${segment(id)}`, { method: 'PUT', body: JSON.stringify(payload) }),
  testAiProvider: (id: string) =>
    request<{ text: string; model: string; durationMs: number }>(`/api/ai/providers/${segment(id)}/test`, { method: 'POST' }),
  analyzeBuild: (buildId: string, providerId?: string) =>
    request<AiAnswer>(`/api/ai/analyze/build/${segment(buildId)}`, {
      method: 'POST',
      body: JSON.stringify({ providerId: providerId ?? null })
    }),
  reviewScript: (payload: {
    command: string
    workingDirectory?: string | null
    jobName?: string | null
    jobId?: string | null
    providerId?: string | null
  }) => request<AiAnswer>('/api/ai/review-script', { method: 'POST', body: JSON.stringify(payload) }),
  aiRequests: (limit = 50) => request<AiRequestLogEntry[]>(`/api/ai/requests?limit=${limit}`),

  actionCatalog: () => request<ActionCatalog>('/api/actions/'),
  previewActions: (actions: ActionStep[]) =>
    request<ActionPreview>('/api/actions/preview', { method: 'POST', body: JSON.stringify({ actions }) }),
  installAction: (manifest: string) =>
    request<ActionDefinition>('/api/actions/install', { method: 'POST', body: JSON.stringify({ manifest }) }),
  uninstallAction: (id: string) => request<void>(`/api/actions/${segment(id)}`, { method: 'DELETE' }),
  actionPacks: () => request<ActionPackList>('/api/actions/packs'),
  reloadActionPacks: () => request<ActionPackList>('/api/actions/packs/reload', { method: 'POST' }),

  localization: () => request<LocalizationPayload>('/api/i18n/'),
  saveTranslation: (locale: string, source: string, value: string) =>
    request<{ cleared: boolean }>('/api/i18n/translations', {
      method: 'PUT',
      body: JSON.stringify({ locale, source, value })
    }),
  saveTranslations: (locale: string, entries: Record<string, string>) =>
    request<{ saved: number }>(`/api/i18n/translations/${segment(locale)}`, {
      method: 'PUT',
      body: JSON.stringify(entries)
    }),
  clearTranslations: (locale: string) =>
    request<{ cleared: number }>(`/api/i18n/translations/${segment(locale)}`, { method: 'DELETE' })
}
