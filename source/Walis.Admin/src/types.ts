export type BuildStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Canceled'
export type TriggerKind = 'manual' | 'push' | 'cron'
export type RunnerStatusKind = 'Idle' | 'Busy' | 'Offline' | 'Paused'
export type NotificationChannelKind = 'email' | 'slack' | 'webhook'

export interface Job {
  id: string
  name: string
  project: string | null
  description: string | null
  repository: string | null
  branch: string
  runnerName: string | null
  workingDirectory: string | null
  command: string
  timeoutMinutes: number
  artifactPath: string | null
  cachePaths: string | null

  /** Null follows the system setting; true and false decide it for this job alone. */
  cleanWorkspace: boolean | null
  /** Clear the workspace after the build (success or failure); null follows the system setting. */
  cleanWorkspaceAfterBuild: boolean | null
  matrixJson: string | null
  viewRole: UserRoleKind
  runRole: UserRoleKind
  triggerKind: TriggerKind
  cron: string | null
  notifyOnFailure: boolean
  isDisabled: boolean
  scriptMode: ScriptMode
  actionsJson: string | null
  createdAt: string
  updatedAt: string
  parametersJson: string | null
  allowConcurrentBuilds: boolean
  downstreamJson: string | null
  /** Adds (deny network*) to the sandbox profile where a sandbox exists. */
  denyNetwork: boolean
  /** Admin only: the job runs without the sandbox on purpose (the self-update job). */
  trustedUnsandboxed: boolean
  totalBuilds: number
  succeededBuilds: number
  successRate: number
  lastBuildNumber: number | null
  lastBuildStatus: BuildStatus | null
  lastBuildAt: string | null
  lastBuildSeconds: number | null
  nextRunAt: string | null
}

export type ScriptMode = 'source' | 'actions'

export type JobSortKey = 'name' | 'project' | 'lastBuild' | 'successRate' | 'updated'

/** The paged job query; filtering, sorting and paging happen on the server, and with thousands of jobs the frontend fetches one page at a time. */
export interface JobFilter {
  search?: string
  status?: 'enabled' | 'paused' | ''
  trigger?: TriggerKind | ''
  project?: string
  sort?: JobSortKey
  order?: 'asc' | 'desc'
  page?: number
  pageSize?: number
}

export interface JobPage {
  items: Job[]
  /** The total matching the filter. */
  total: number
  page: number
  pageSize: number
  /** The following are limited only by permissions, not by the filter: for the header statistics and the project dropdown. */
  totalAll: number
  enabledAll: number
  projects: string[]
}

export interface JobInput {
  name: string
  project: string | null
  description: string | null
  repository: string | null
  branch: string
  runnerName: string | null
  workingDirectory: string | null
  command: string
  timeoutMinutes: number
  artifactPath: string | null
  cachePaths: string | null

  /** Null follows the system setting; true and false decide it for this job alone. */
  cleanWorkspace: boolean | null
  /** Clear the workspace after the build (success or failure); null follows the system setting. */
  cleanWorkspaceAfterBuild: boolean | null
  matrixJson: string | null
  viewRole: UserRoleKind
  runRole: UserRoleKind
  triggerKind: TriggerKind
  cron: string | null
  notifyOnFailure: boolean
  isDisabled: boolean
  scriptMode: ScriptMode
  actions: ActionStep[]
  parametersJson: string | null
  allowConcurrentBuilds: boolean
  downstreamJson: string | null
  denyNetwork: boolean
  trustedUnsandboxed: boolean
}

export type BuildParameterType = 'string' | 'choice' | 'boolean' | 'text' | 'password'

/** The build parameters the job declares, matching Jenkins's parameters {}; values are decided at trigger time. */
export interface BuildParameterDefinition {
  name: string
  label?: string | null
  type: BuildParameterType
  default?: string | null
  description?: string | null
  options?: string[]
}

export type DownstreamWhen = 'success' | 'failure' | 'always'

/** Downstream jobs to trigger when this job finishes, referenced by name. */
export interface DownstreamTarget {
  jobName: string
  when: DownstreamWhen
}

export type StepRunCondition = 'success' | 'always' | 'failure'

/** Schedule timeline: each Cron job's trigger times and actual builds within the window. */
export interface ScheduleTimeline {
  from: string
  to: string
  date: string
  days: number
  timeZone: string
  jobs: ScheduleTimelineJob[]
}

export interface ScheduleTimelineJob {
  id: string
  name: string
  project: string | null
  triggerKind: TriggerKind
  cron: string | null
  isDisabled: boolean
  cronError: string | null
  averageSeconds: number | null
  lastBuildSeconds: number | null
  starts: string[]
  builds: ScheduleTimelineBuild[]
}

export interface ScheduleTimelineBuild {
  id: string
  number: number
  status: BuildStatus
  triggeredBy: string
  queuedAt: string
  startedAt: string | null
  finishedAt: string | null
  durationSeconds: number | null
  matrixLabel: string | null
}

/** The preview result of a Jenkins import. */
export interface JenkinsImportResult {
  format: string
  job: JenkinsJobDraft
  environment: JenkinsEnvironmentVariable[]
  warnings: JenkinsWarning[]
}

export interface JenkinsJobDraft {
  name: string
  description: string | null
  repository: string | null
  branch: string
  runnerName: string | null
  timeoutMinutes: number
  triggerKind: TriggerKind
  cron: string | null
  artifactPath: string | null
  matrixJson: string | null
  parametersJson: string | null
  allowConcurrentBuilds: boolean
  downstreamJson: string | null
  isDisabled: boolean
  actions: ActionStep[]
}

export interface JenkinsEnvironmentVariable {
  name: string
  value: string
  isSecret: boolean
  source: string | null
}

export interface JenkinsWarning {
  severity: 'info' | 'warning' | 'unsupported'
  message: string
  line: number | null
  snippet: string | null
}

export interface Build {
  id: string
  jobId: string
  number: number
  status: BuildStatus
  triggeredBy: string
  runnerName: string | null
  queuedAt: string
  startedAt: string | null
  finishedAt: string | null
  exitCode: number | null
  log: string
  jobName: string
  jobProject: string | null
  matrixLabel: string | null
  /** The build parameter values at trigger time (excluding the password type). */
  parametersJson: string | null
  /** The caller's idempotency reference (externalRef / Idempotency-Key); shared by every build of one matrix trigger. */
  externalRef?: string | null
  /** Set once someone asked a running build to stop; the executor ends it as Canceled shortly after. */
  cancelRequestedAt?: string | null
  durationSeconds: number | null

  /** Present only when querying a single build; lists omit steps to stay lightweight. */
  steps: BuildStep[]
}

export interface Runner {
  id: string
  name: string
  operatingSystem: string
  version: string
  labels: string
  totalSlots: number
  isPaused: boolean
  isLocal: boolean
  lastSeenAt: string
  createdAt: string
  usedSlots: number
  status: RunnerStatusKind
  currentBuildNumber: number | null
  currentJobName: string | null
  labelList: string[]
  /** The lowest job run role this runner accepts; admin reserves the host for administrator jobs. */
  minimumRunRole?: 'operator' | 'admin'
}

export interface RunnerInput {
  name: string
  operatingSystem: string | null
  version: string | null
  labels: string | null
  totalSlots: number
  isPaused: boolean
  minimumRunRole?: 'operator' | 'admin'
}

export interface EnvironmentVariable {
  id: string
  name: string
  value: string
  isSecret: boolean
  jobId: string | null
  jobName: string | null
  updatedAt: string
}

export interface EnvironmentVariableInput {
  name: string
  value: string
  isSecret: boolean
  jobId: string | null
}

export interface NotificationChannel {
  id: string
  kind: NotificationChannelKind
  name: string
  /** Slack and webhook URLs come back as scheme://host/*** (targetMasked); sending that back unchanged keeps the stored URL. */
  target: string
  targetMasked?: boolean
  isEnabled: boolean
  updatedAt: string
}

export interface NotificationLogEntry {
  id: string
  sentAt: string
  event: string
  channelKind: string
  target: string
  succeeded: boolean
  detail: string | null
}

export interface WalisSettings {
  siteName: string
  baseUrl: string
  timeZone: string
  locale: string
  maxConcurrentBuilds: number
  defaultTimeoutMinutes: number
  logRetentionDays: number
  /** Absent for viewers: the server path is only returned to operators and administrators. */
  defaultWorkingDirectory?: string | null
  defaultShell: string | null
  keepArtifacts: boolean
  cleanWorkspace: boolean
  cleanWorkspaceAfterBuild: boolean
  requireSignIn: boolean
  requireOtp: boolean
  auditLog: boolean
  auditRetentionDays: number
  maskSecrets: boolean
  /** Whether operators may use the AI features that send scripts and build output to the provider; admins always may. */
  allowOperatorAi: boolean
  corsOrigins: string
  /** Source addresses allowed to reach /mcp, one IP or CIDR per line; empty means unrestricted. */
  mcpAllowedAddresses: string
  webhookSecret: string | null
  notificationSigningSecret: string | null
  notifyOnFailure: boolean
  notifyOnRecovery: boolean
  notifyOnSuccess: boolean
  notifyOnTimeout: boolean
  notifyOnRunnerOffline: boolean
  notifyOnSecurityAlert: boolean
  quietHoursStart: string
  quietHoursEnd: string
  smtpHost: string | null
  smtpPort: number
  smtpUseSsl: boolean
  smtpUser: string | null
  smtpPassword: string | null
  smtpFrom: string | null
  isolateBuilds: boolean
  allowUnisolatedBuilds: boolean
  /** An administrator accepted that unisolated builds let anyone who can edit or run a job read every secret. */
  unisolatedBuildsAcknowledged: boolean
  /** Stamped by the server; the client only toggles the switch. */
  unisolatedBuildsAcknowledgedAt: string | null
  unisolatedBuildsAcknowledgedBy: string | null
  /** macOS sandbox: also deny signals to other processes, Mach lookups and reading other processes' information. */
  sandboxDenyProcessControl: boolean
  externallyIsolatedRunners: string
  sandboxDeniedPaths: string
  allowedWorkingDirectories: string
  backupEnabled: boolean
  backupCron: string
  backupDirectory: string | null
  backupKeepDays: number
  backupPassphrase: string | null
  backupPaths: string
  backupPostgresEnabled: boolean
  backupPostgresHost: string
  backupPostgresPort: number
  backupPostgresUser: string | null
  backupPostgresPassword: string | null
  backupPostgresDatabases: string
  backupPgDumpPath: string | null
  backupTargets: BackupTarget[]
}

/** Secret fields are the mask string in API responses; sending them back unchanged means not modified. */
export const MASKED = '********'

export type BackupProvider = 'local' | 's3' | 'dropbox' | 'google-drive' | 'onedrive'

export interface PluginPermissions {
  http: string[]
  secrets: string[]
  kv: boolean
}

export interface PluginManifest {
  id: string
  name: string
  description: string
  version: string
  author: string
  apiVersion: number
  kind: string
  category: string
  icon: string
  parameters: unknown[]
  permissions: PluginPermissions
}

export interface PluginSummary {
  pluginId: string
  manifest: PluginManifest
  isDisabled: boolean
  isTrusted: boolean
  installedAt: string
  updatedAt: string
  codeLength: number
}

export interface ApiKey {
  id: string
  name: string
  prefix: string
  role: UserRoleKind
  scope: 'mcp' | 'api' | 'runner'
  isDisabled: boolean
  createdAt: string
  createdBy: string | null
  expiresAt: string | null
  /** When set, this key can only claim and report builds for that runner. */
  runnerName: string | null
  /** Source addresses the key may be presented from, one IP or CIDR per line; null means any. */
  allowedAddresses: string | null
  lastUsedAt: string | null
}

export type BuildStepStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped'

export interface BuildStep {
  id: string
  buildId: string
  number: number
  name: string
  command: string
  continueOnError: boolean
  parallel: boolean
  runWhen: StepRunCondition
  retries: number
  /** The key within the flow; outputs of a keyed step are injected as WALIS_STEP_<key>_<name>. */
  stepKey: string | null
  /** The step numbers this depends on; when empty the build derives them from order and parallel. */
  dependsOn: number[]
  /** A condition evaluated at run time; empty means none. */
  condition: string | null
  /** The outputs the step wrote to $WALIS_OUTPUT_FILE. */
  outputs: Record<string, string>
  status: BuildStepStatus
  startedAt: string | null
  finishedAt: string | null
  exitCode: number | null
  log: string
  durationSeconds: number | null
}

export interface BackupTarget {
  id: string
  name: string
  provider: BackupProvider
  isDisabled: boolean
  path: string
  bucket: string | null
  region: string | null
  endpoint: string | null
  accessKeyId: string | null
  secretAccessKey: string | null
  clientId: string | null
  clientSecret: string | null
  refreshToken: string | null
}

export interface BackupRun {
  id: string
  startedAt: string
  finishedAt: string | null
  status: 'Running' | 'Succeeded' | 'Failed'
  triggeredBy: string
  sizeBytes: number
  fileName: string | null
  log: string
  durationSeconds: number | null
}

export interface BackupPayload {
  isRunning: boolean
  defaultDirectory: string
  runs: BackupRun[]
}

/** Files in the backup directory that can be restored from. */
export interface BackupFile {
  name: string
  sizeBytes: number
  modifiedAt: string
}

export interface BackupFilesPayload {
  directory: string
  files: BackupFile[]
}

export interface RestoreResult {
  tables: number
  rows: number
  artifacts: number
  provider: string
  /** True when the sidecar or the backup's own AEAD authentication succeeded. */
  integrityVerified: boolean | null
}

/** Secrets at rest: where the master key comes from and which stored values it cannot read (admins only). */
export interface SecretsStatus {
  keySource: string
  keyPath: string | null
  undecryptable: { table: string; item: string }[]
  count: number
}

export interface SettingsPayload {
  settings: WalisSettings
  /** Admins only, with the password already masked. */
  databasePath: string | null
  timeZones: string[]
  /** The service version, e.g. 1.0.0+4c90a97; the UI and the API are the same release. */
  version: string
  /** Whether the server's platform has a sandbox Walis can apply to builds (macOS only for now). */
  sandboxAvailable: boolean
}

export interface Locale {
  code: string
  name: string
  nativeName: string
  isEnabled: boolean
  sortOrder: number
  updatedAt: string
  isSource: boolean
}

export interface LocalizationPayload {
  locales: Locale[]
  overrides: Record<string, Record<string, string>>
}

export interface DailyBuildStat {
  date: string
  total: number
  succeeded: number
  failed: number
}

export interface ActivityEntry {
  kind: BuildStatus
  jobName: string
  buildNumber: number | null
  runnerName: string | null
  occurredAt: string
}

export interface DashboardSummary {
  totalJobs: number
  enabledJobs: number
  queuedBuilds: number
  runningBuilds: number
  successfulBuilds: number
  failedBuilds: number
  buildsLast7Days: number
  failuresLast7Days: number
  successRateLast7Days: number
  averageDurationSeconds: number
  averageQueueSeconds: number
  activeSchedules: number
  trend: DailyBuildStat[]
  recentBuilds: Build[]
  activity: ActivityEntry[]
  runners: Runner[]
  jobs: Job[]
}

export interface BuildFilter {
  status?: string
  runner?: string
  jobId?: string
  search?: string
  limit?: number
  page?: number
  pageSize?: number
}

export interface BuildPage {
  items: Build[]
  /** The total matching the filter. */
  total: number
  page: number
  pageSize: number
  /** Builds per status; limited by permissions and the other filters, but never by the status filter. */
  counts: Partial<Record<BuildStatus, number>>
}

export interface BuildArtifact {
  id: string
  buildId: string
  relativePath: string
  sizeBytes: number
  createdAt: string
}

export interface AuditEntry {
  id: string
  occurredAt: string
  actor: string
  action: string
  entityType: string
  entityId: string | null
  summary: string
  /** The client address the server saw; null for background work such as the scheduler. */
  remoteAddress: string | null
  outcome: 'success' | 'denied' | 'failed'
  requestId: string | null
  jobId: string | null
}

export interface AuditFilter {
  actor?: string
  entityType?: string
  entityId?: string
  action?: string
  outcome?: string
  /** ISO 8601, UTC; the range is [from, to). */
  from?: string
  to?: string
  page?: number
  pageSize?: number
}

export interface AuditPage {
  items: AuditEntry[]
  total: number
  page: number
  pageSize: number
}

export interface SecurityPostureCheck {
  id: string
  status: 'pass' | 'warn' | 'danger'
  domain: string
  title: string
  detail: string
  action: string
}

export interface SecurityPosture {
  status: 'good' | 'warn' | 'danger'
  passedChecks: number
  warnChecks: number
  dangerChecks: number
  totalChecks: number
  sandboxAvailable: boolean
  isolationMode: 'sandboxed' | 'strict-external' | 'unisolated-acknowledged' | 'unisolated'
  requireHttps: boolean
  outboundAllowPrivateNetwork: boolean
  checks: SecurityPostureCheck[]
}

export interface SecurityPrivilegedJob {
  id: string
  name: string
  project: string | null
  runner: string | null
  trustedUnsandboxed: boolean
  denyNetwork: boolean
  runRole: UserRoleKind
  viewRole: UserRoleKind
  isDisabled: boolean
}

export interface SecurityTrustedPlugin {
  pluginId: string
  name: string
  version: string
  isDisabled: boolean
  isTrusted: boolean
  httpPermissions: string[]
  secretPermissions: string[]
  installedAt: string
  updatedAt: string
}

export interface SecurityRiskyApiKey {
  id: string
  name: string
  prefix: string
  role: UserRoleKind
  scope: 'mcp' | 'api' | 'runner'
  runnerName: string | null
  allowedAddresses: string | null
  expiresAt: string | null
  lastUsedAt: string | null
  createdAt: string
  riskFlags: string[]
}

export interface SecurityAccountItem {
  id: string
  username: string
  displayName: string
  role: UserRoleKind
  isDisabled: boolean
  hasOtp: boolean
  totpEnabledAt: string | null
  lastLoginAt: string | null
  createdAt: string
}

export interface SecurityRunnerItem {
  id: string
  name: string
  operatingSystem: string
  status: RunnerStatusKind
  isLocal: boolean
  isPaused: boolean
  externalIsolation: boolean
  serverApprovedExternal: boolean
}

export interface SecurityInventory {
  privilegedJobs: SecurityPrivilegedJob[]
  trustedPlugins: SecurityTrustedPlugin[]
  riskyApiKeys: SecurityRiskyApiKey[]
  accounts: SecurityAccountItem[]
  runners: SecurityRunnerItem[]
}

export interface ActiveSignInLockout {
  bucketKey: string
  kind: 'account' | 'address'
  target: string
  failures: number
  limit: number
  firstFailureAt: string
  lockedUntil: string
  remainingSeconds: number
}

export interface SecurityAuditMetrics {
  signInFailures: number
  lockouts: number
  denied: number
  secretReveals: number
}

export interface SecurityThreats {
  activeLockouts: ActiveSignInLockout[]
  metrics24h: SecurityAuditMetrics
  recentEvents: AuditEntry[]
}

export interface SecurityCenterPayload {
  posture: SecurityPosture
  inventory: SecurityInventory
  threats: SecurityThreats
}

export type UserRoleKind = 'admin' | 'operator' | 'viewer'

export interface WalisUser {
  id: string
  username: string
  displayName: string
  role: UserRoleKind
  isDisabled: boolean
  hasOtp: boolean
  totpEnabledAt: string | null
  createdAt: string
  lastLoginAt: string | null
}

export interface SignedInUser {
  username: string
  displayName: string
  role: UserRoleKind
  hasOtp: boolean
}

export interface AuthStatus {
  requireSignIn: boolean
  requireOtp: boolean
  hasUsers: boolean
  /** No administrator yet and this browser is not on the server itself, so setup needs the setup code. */
  setupCodeRequired: boolean
  /** No administrator yet and builds may run without an isolation boundary: the setup screen explains and asks. */
  unisolatedBuildsAllowed?: boolean
  user: SignedInUser | null
}

/** A browser sign-in never carries a token: the session lives in the HttpOnly cookie, and only `?bearer=true` CLI sign-ins get one. */
export interface SignInResult {
  expiresAt?: string
  usedRecoveryCode?: boolean
  otpRequired?: boolean
  challenge?: string
  user?: SignedInUser
}

export interface OtpEnrollment {
  secret: string
  uri: string
  digits: number
  period: number
}

export type AiProviderKindName = 'gemini' | 'claude' | 'codex'

export interface AiProvider {
  id: string
  kind: AiProviderKindName
  name: string
  model: string
  baseUrl: string | null
  isEnabled: boolean
  isDefault: boolean
  updatedAt: string
  hasApiKey: boolean
  isUsable: boolean
  defaultModel: string
}

export interface AiProviderInput {
  apiKey: string | null
  model: string
  baseUrl: string | null
  isEnabled: boolean
  isDefault: boolean
}

export interface AiAnswer {
  text: string
  providerKind: string
  model: string
  inputTokens: number | null
  outputTokens: number | null
  durationMs: number
}

export interface AiRequestLogEntry {
  id: string
  startedAt: string
  providerKind: string
  model: string
  purpose: string
  buildId: string | null
  jobId: string | null
  jobName: string | null
  actor: string
  promptChars: number
  inputTokens: number | null
  outputTokens: number | null
  durationMs: number
  succeeded: boolean
  error: string | null
}

export type ActionParameterKind = 'text' | 'multiline' | 'number' | 'boolean' | 'select' | 'path'

export interface ActionOption {
  value: string
  label: string
}

export interface ActionParameter {
  name: string
  label: string
  type: ActionParameterKind
  required: boolean
  default: string | null
  placeholder: string | null
  help: string | null
  options: ActionOption[]
}

export interface ActionOutput {
  name: string
  description: string
}

/** A source later steps can reference: a keyed step and the outputs it declares. */
export interface StepReferenceSource {
  key: string
  title: string
  outputs: ActionOutput[]
}

export interface ActionDefinition {
  id: string
  name: string
  description: string
  category: string
  icon: string
  version: string
  author: string
  parameters: ActionParameter[]
  /** The outputs an action declares it produces, offered when picking a reference. */
  outputs?: ActionOutput[]
  template: string
  isBuiltIn: boolean
  /** `pack:<pack name>` for actions from an action pack; built-in and UI-installed ones have none. */
  source?: string | null
}

export interface ActionPack {
  name: string
  version: string
  description: string
  directory: string
  count: number
  ids: string[]
  errors: string[]
}

export interface ActionPackList {
  roots: string[]
  packs: ActionPack[]
}

export interface ActionStep {
  id: string
  actionId: string
  name: string | null
  /** The key (lowercase alphanumerics and underscores); later steps reference this step's outputs with ${{ steps.<key>.<output> }}. */
  key?: string | null
  enabled: boolean
  continueOnError: boolean
  /** Parallel with the previous group; ignored when the flow has explicit dependencies (dependsOn). */
  parallel: boolean
  /** The step ids this depends on. Derived from order and parallel when none in the flow is set; once set the graph is authoritative and unset steps are entry points. */
  dependsOn?: string[] | null
  /** Run condition: success (default), always, failure. */
  runWhen?: StepRunCondition | null
  /** Retries after failure, 0 to 10. */
  retries?: number
  /** A condition evaluated at run time, e.g. steps.check.changed == 'true'; the step is skipped when false. */
  condition?: string | null
  parameters: Record<string, string | null>
}

export interface ActionCatalog {
  categories: string[]
  definitions: ActionDefinition[]
}

export interface ActionPreview {
  script: string
  errors: string[]
}
