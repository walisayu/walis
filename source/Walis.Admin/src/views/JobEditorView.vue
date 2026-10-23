<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import ActionFlowEditor from '../components/actions/ActionFlowEditor.vue'
import AiAnswerModal from '../components/AiAnswerModal.vue'
import BuildParametersEditor from '../components/BuildParametersEditor.vue'
import DownstreamEditor from '../components/DownstreamEditor.vue'
import { ApiError, api } from '../services/api'
import { useActionsStore } from '../stores/actions'
import { useAiStore } from '../stores/ai'
import { useAuthStore } from '../stores/auth'
import { useImportDraftStore } from '../stores/importDraft'
import { useJobsStore } from '../stores/jobs'
import { useRunnersStore } from '../stores/runners'
import { useSettingsStore } from '../stores/settings'
import { useToastStore } from '../stores/toasts'
import type { ActionStep, AiAnswer, JenkinsEnvironmentVariable, JobInput, ScriptMode, TriggerKind } from '../types'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const jobs = useJobsStore()
const runners = useRunnersStore()
const settings = useSettingsStore()
const actions = useActionsStore()
const ai = useAiStore()
const auth = useAuthStore()
const toasts = useToastStore()
const importDraft = useImportDraftStore()

/** Environment variables to create after the job is created when importing from Jenkins. */
const pendingEnvironment = ref<JenkinsEnvironmentVariable[]>([])
const importedFrom = ref<string | null>(null)

const jobId = computed(() => (route.params.id as string | undefined) ?? null)

/** Creating from a copy: the job the new one is filled from, chosen by the copy button in the job list. */
const copyFromId = computed(() => (typeof route.query.copy === 'string' ? route.query.copy : null))
const isEdit = computed(() => jobId.value !== null)
const loading = ref(true)
const saving = ref(false)
const errors = ref<Record<string, string[]>>({})
const commandExpanded = ref(false)
const expandedCommand = ref('')
const switchWarning = ref(false)

type EditorTab = 'basic' | 'script' | 'build' | 'trigger'

const tabs: { key: EditorTab; label: string; icon: string }[] = [
  { key: 'basic', label: '基本資料', icon: 'cil-info' },
  { key: 'script', label: '執行腳本', icon: 'cil-terminal' },
  { key: 'build', label: '建置設定', icon: 'cil-settings' },
  { key: 'trigger', label: '觸發與通知', icon: 'cil-bell' }
]

const tabOfField: Record<string, EditorTab> = {
  name: 'basic', project: 'basic', branch: 'basic', description: 'basic', repository: 'basic',
  command: 'script', actions: 'script',
  runnerName: 'build', workingDirectory: 'build', timeoutMinutes: 'build', artifactPath: 'build',
  cachePaths: 'build', matrixJson: 'build', parametersJson: 'build', viewRole: 'build', runRole: 'build', roles: 'build',
  triggerKind: 'trigger', cron: 'trigger', downstreamJson: 'trigger'
}

const initialTab = route.query.tab
const tab = ref<EditorTab>(tabs.some((item) => item.key === initialTab) ? (initialTab as EditorTab) : 'basic')

function tabHasError(key: EditorTab): boolean {
  return Object.keys(errors.value).some((field) => (tabOfField[field] ?? 'basic') === key)
}

const aiVisible = ref(false)
const aiAnswer = ref<AiAnswer | null>(null)
const aiLoading = ref(false)
const aiError = ref<string | null>(null)

// The clean-workspace select stores a string because CoreUI only reports an option's DOM value; submit converts it
// back into the tri-state the API expects, where null means "follow the system setting".
type CleanWorkspaceChoice = '' | 'true' | 'false'
type JobForm = Omit<JobInput, 'cleanWorkspace' | 'cleanWorkspaceAfterBuild'> & {
  cleanWorkspace: CleanWorkspaceChoice
  cleanWorkspaceAfterBuild: CleanWorkspaceChoice
}

function toCleanChoice(value: boolean | null | undefined): CleanWorkspaceChoice {
  return value === null || value === undefined ? '' : value ? 'true' : 'false'
}

// Password parameters and job-scoped secrets end up in the workspace (checked-out sources, files the script writes), so the
// editor suggests cleaning after the build for such jobs. It is only a hint; the choice stays with the user.
const hasJobSecrets = ref(false)

const hasPasswordParameters = computed(() => {
  if (!form.parametersJson) return false

  try {
    const parsed = JSON.parse(form.parametersJson) as unknown
    return Array.isArray(parsed) && parsed.some((item) => (item as { type?: string })?.type === 'password')
  } catch {
    return false
  }
})

const suggestCleanAfterBuild = computed(
  () =>
    (hasPasswordParameters.value || hasJobSecrets.value) &&
    form.cleanWorkspaceAfterBuild !== 'true' &&
    !(form.cleanWorkspaceAfterBuild === '' && settings.settings?.cleanWorkspaceAfterBuild)
)

async function loadJobSecrets(id: string): Promise<void> {
  try {
    const variables = await api.environmentVariables()
    hasJobSecrets.value = variables.some((variable) => variable.isSecret && variable.jobId === id)
  } catch {
    hasJobSecrets.value = false
  }
}

const form = reactive<JobForm>({
  name: '',
  project: '',
  description: '',
  repository: '',
  branch: 'main',
  runnerName: '',
  workingDirectory: '',
  command: '',
  timeoutMinutes: 30,
  artifactPath: '',
  cachePaths: '',

  // '' follows the system setting; the select stores strings because CoreUI only reports an option's DOM value.
  cleanWorkspace: '',
  cleanWorkspaceAfterBuild: '',
  matrixJson: '',
  viewRole: 'viewer',
  runRole: 'operator',
  triggerKind: 'manual',
  cron: '',
  notifyOnFailure: true,
  isDisabled: false,
  scriptMode: 'actions',
  actions: [],
  parametersJson: '',
  allowConcurrentBuilds: true,
  downstreamJson: '',
  denyNetwork: false,
  trustedUnsandboxed: false
})

const cronPresets = [
  { label: '每 15 分鐘', value: '*/15 * * * *' },
  { label: '每日凌晨 2:30', value: '30 2 * * *' },
  { label: '每週五 18:00', value: '0 18 * * 5' },
  { label: '每月 1 號 00:00', value: '0 0 1 * *' }
]

function fieldError(field: string): string | undefined {
  return errors.value[field]?.[0]
}

onMounted(async () => {
  await Promise.all([runners.load(), settings.load(), actions.load(), jobs.load()])

  form.timeoutMinutes = settings.settings?.defaultTimeoutMinutes ?? 30
  form.runnerName = runners.items.find((runner) => runner.isLocal)?.name ?? ''

  if (jobId.value) {
    await jobs.load()
    const job = jobs.byId(jobId.value)
    void loadJobSecrets(jobId.value)

    if (job) {
      Object.assign(form, {
        name: job.name,
        project: job.project ?? '',
        description: job.description ?? '',
        repository: job.repository ?? '',
        branch: job.branch,
        runnerName: job.runnerName ?? '',
        workingDirectory: job.workingDirectory ?? '',
        command: job.command,
        timeoutMinutes: job.timeoutMinutes,
        artifactPath: job.artifactPath ?? '',
        cachePaths: job.cachePaths ?? '',
        cleanWorkspace: toCleanChoice(job.cleanWorkspace),
        cleanWorkspaceAfterBuild: toCleanChoice(job.cleanWorkspaceAfterBuild),
        matrixJson: job.matrixJson ?? '',
        viewRole: job.viewRole ?? 'viewer',
        runRole: job.runRole ?? 'operator',
        triggerKind: job.triggerKind,
        cron: job.cron ?? '',
        notifyOnFailure: job.notifyOnFailure,
        isDisabled: job.isDisabled,
        scriptMode: job.scriptMode,
        actions: parseActions(job.actionsJson),
        parametersJson: job.parametersJson ?? '',
        allowConcurrentBuilds: job.allowConcurrentBuilds ?? true,
        downstreamJson: job.downstreamJson ?? '',
        denyNetwork: job.denyNetwork ?? false,
        trustedUnsandboxed: job.trustedUnsandboxed ?? false
      })
    }
  } else if (copyFromId.value && jobs.byId(copyFromId.value)) {
    // Copying a job: everything but the identity is carried over, and it is saved only after the user confirms.
    const source = jobs.byId(copyFromId.value)!

    Object.assign(form, {
      name: t('{name} 的複本', { name: source.name }).slice(0, 80),
      project: source.project ?? '',
      description: source.description ?? '',
      repository: source.repository ?? '',
      branch: source.branch,
      runnerName: source.runnerName ?? '',
      workingDirectory: source.workingDirectory ?? '',
      command: source.command,
      timeoutMinutes: source.timeoutMinutes,
      artifactPath: source.artifactPath ?? '',
      cachePaths: source.cachePaths ?? '',
      cleanWorkspace: toCleanChoice(source.cleanWorkspace),
      cleanWorkspaceAfterBuild: toCleanChoice(source.cleanWorkspaceAfterBuild),
      matrixJson: source.matrixJson ?? '',
      viewRole: source.viewRole ?? 'viewer',
      runRole: source.runRole ?? 'operator',
      triggerKind: source.triggerKind,
      cron: source.cron ?? '',
      notifyOnFailure: source.notifyOnFailure,
      isDisabled: source.isDisabled,
      scriptMode: source.scriptMode,
      actions: parseActions(source.actionsJson),
      parametersJson: source.parametersJson ?? '',
      allowConcurrentBuilds: source.allowConcurrentBuilds ?? true,
      downstreamJson: source.downstreamJson ?? '',
      denyNetwork: source.denyNetwork ?? false,
      // Trust is never copied along: an administrator decides it for each job on purpose.
      trustedUnsandboxed: false
    })
  } else if (importDraft.job) {
    // Import from Jenkins: the whole draft is brought in and saved only after the user confirms.
    const { job: draft, environment } = importDraft.take()!
    Object.assign(form, {
      name: draft.name,
      description: draft.description ?? '',
      repository: draft.repository ?? '',
      branch: draft.branch || 'main',
      runnerName: draft.runnerName ?? form.runnerName,
      timeoutMinutes: draft.timeoutMinutes,
      artifactPath: draft.artifactPath ?? '',
      matrixJson: draft.matrixJson ?? '',
      triggerKind: draft.triggerKind,
      cron: draft.cron ?? '',
      isDisabled: draft.isDisabled,
      scriptMode: 'actions' as ScriptMode,
      actions: draft.actions,
      parametersJson: draft.parametersJson ?? '',
      allowConcurrentBuilds: draft.allowConcurrentBuilds,
      downstreamJson: draft.downstreamJson ?? ''
    })
    pendingEnvironment.value = environment
    importedFrom.value = 'jenkins'
  } else {
    // A new job starts in flow mode by default with a single run-command step.
    const shell = actions.byId.get('shell.run')
    form.actions = shell ? [actions.createStep(shell)] : []
  }

  loading.value = false
})

/** Environment variables brought in by the import: those with values become job-specific variables directly, secrets without values remind the user to fill them in. */
async function createImportedEnvironment(jobId: string): Promise<void> {
  const missing: string[] = []
  let created = 0

  for (const variable of pendingEnvironment.value) {
    if (!variable.value) {
      missing.push(variable.name)
      continue
    }

    try {
      await api.createEnvironmentVariable({ name: variable.name, value: variable.value, isSecret: variable.isSecret, jobId })
      created += 1
    } catch (error) {
      toasts.error(t('環境變數 {name} 建立失敗：{message}', { name: variable.name, message: error instanceof Error ? error.message : String(error) }))
    }
  }

  if (created > 0) {
    toasts.success(t('已建立 {count} 個任務專屬環境變數', { count: created }))
  }

  if (missing.length > 0) {
    toasts.push(t('請到環境變數頁替這個任務填入機密：{names}', { names: missing.join('、') }), 'warning')
  }

  pendingEnvironment.value = []
}

function parseActions(json: string | null): ActionStep[] {
  if (!json) {
    return []
  }

  try {
    return JSON.parse(json) as ActionStep[]
  } catch {
    return []
  }
}

/** When switching to script mode, compile the flow first as the starting point. */
async function switchToSource(): Promise<void> {
  if (form.actions.length > 0) {
    try {
      const preview = await actions.preview(form.actions)
      form.command = preview.script
    } catch (error) {
      toasts.error(error instanceof Error ? error.message : String(error))
      return
    }
  }

  form.scriptMode = 'source'
}

/** Switching back to flow mode: reuse the existing flow, or wrap the current script into a single step when there is none. */
function switchToActions(): void {
  if (form.actions.length === 0) {
    const shell = actions.byId.get('shell.run')

    if (shell) {
      const step = actions.createStep(shell)
      step.parameters.command = form.command
      form.actions = [step]
    }
  } else {
    switchWarning.value = true
  }

  form.scriptMode = 'actions'
}

async function setMode(mode: ScriptMode): Promise<void> {
  if (mode === form.scriptMode) {
    return
  }

  switchWarning.value = false

  if (mode === 'source') {
    await switchToSource()
  } else {
    switchToActions()
  }
}

function openCommandEditor(): void {
  expandedCommand.value = form.command
  commandExpanded.value = true
}

function applyCommand(): void {
  form.command = expandedCommand.value
  commandExpanded.value = false
  toasts.info(t('已套用命令內容'))
}

async function reviewScript(): Promise<void> {
  let script = form.command

  if (form.scriptMode === 'actions') {
    try {
      script = (await actions.preview(form.actions)).script
    } catch (error) {
      toasts.error(error instanceof Error ? error.message : String(error))
      return
    }
  }

  if (script.trim().length === 0) {
    toasts.error(t('請先輸入要檢查的命令。'))
    return
  }

  aiVisible.value = true
  aiLoading.value = true
  aiError.value = null
  aiAnswer.value = null

  try {
    aiAnswer.value = await api.reviewScript({
      command: script,
      workingDirectory: form.workingDirectory || null,
      jobName: form.name || null,
      jobId: jobId.value
    })
  } catch (exception) {
    aiError.value = exception instanceof ApiError ? exception.message : String(exception)
  } finally {
    aiLoading.value = false
    await ai.loadRequests().catch(() => undefined)
  }
}

async function submit(): Promise<void> {
  saving.value = true
  errors.value = {}

  const payload: JobInput = {
    ...form,
    project: form.project || null,
    description: form.description || null,
    repository: form.repository || null,
    runnerName: form.runnerName || null,
    workingDirectory: form.workingDirectory || null,
    artifactPath: form.artifactPath || null,
    cachePaths: form.cachePaths || null,
    cleanWorkspace: form.cleanWorkspace === '' ? null : form.cleanWorkspace === 'true',
    cleanWorkspaceAfterBuild: form.cleanWorkspaceAfterBuild === '' ? null : form.cleanWorkspaceAfterBuild === 'true',
    matrixJson: form.matrixJson || null,
    viewRole: form.viewRole,
    runRole: form.runRole,
    cron: form.triggerKind === 'cron' ? form.cron : null,
    actions: [...form.actions],
    parametersJson: form.parametersJson || null,
    downstreamJson: form.downstreamJson || null
  }

  try {
    if (jobId.value) {
      await jobs.update(jobId.value, payload)
      toasts.success(t('任務已更新'))
    } else {
      const created = await jobs.create(payload)
      toasts.success(t('任務已建立'))
      if (pendingEnvironment.value.length > 0) {
        await createImportedEnvironment(created.id)
      }
    }

    await router.push('/jobs')
  } catch (error) {
    if (error instanceof ApiError) {
      errors.value = error.errors
      toasts.error(error.message)
      // When the error is on another tab, switch to it, otherwise only the red text at the top is visible and the field cannot be found.
      const firstErrorTab = tabs.find((item) => tabHasError(item.key))?.key
      if (firstErrorTab && !tabHasError(tab.value)) {
        tab.value = firstErrorTab
      }
    } else {
      toasts.error(error instanceof Error ? error.message : String(error))
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div v-if="loading" class="d-flex justify-content-center py-5">
    <CSpinner color="primary" />
  </div>

  <CForm v-else novalidate @submit.prevent="submit">
    <CCard class="mb-4">
      <CAlert v-if="importedFrom === 'jenkins'" color="info" class="mb-0 rounded-0 border-0 border-bottom">
        {{ t('這是從 Jenkins 匯入的草稿，還沒有儲存。請檢查步驟、參數與觸發方式，確認後按「建立任務」。') }}
      </CAlert>
      <CCardHeader class="pb-0">
        <CNav variant="underline-border" role="tablist" class="card-header-tabs">
          <CNavItem v-for="item in tabs" :key="item.key">
            <button
              type="button"
              class="nav-link"
              role="tab"
              :class="{ active: tab === item.key }"
              :aria-selected="tab === item.key"
              @click="tab = item.key"
            >
              <CIcon :icon="item.icon" class="me-1" />{{ t(item.label) }}
              <span v-if="tabHasError(item.key)" class="badge rounded-pill text-bg-danger ms-1" :title="t('這個分頁有欄位需要修正')">!</span>
            </button>
          </CNavItem>
        </CNav>
      </CCardHeader>

      <CCardBody v-show="tab === 'basic'">
        <p class="text-body-secondary">{{ t('這些資訊會顯示在任務清單與建置紀錄中。') }}</p>
        <CRow class="g-3">
          <CCol :md="4">
            <CFormLabel for="job-name">{{ t('任務名稱') }} <span class="text-danger">*</span></CFormLabel>
            <CFormInput
              id="job-name"
              v-model="form.name"
              maxlength="80"
              :invalid="Boolean(fieldError('name'))"
              :placeholder="t('例如：管理端正式版建置')"
            />
            <CFormFeedback invalid>{{ fieldError('name') }}</CFormFeedback>
          </CCol>
          <CCol :md="4">
            <CFormLabel for="job-project">{{ t('所屬專案') }}</CFormLabel>
            <CFormInput id="job-project" v-model="form.project" :placeholder="t('例如：Walis Admin')" />
          </CCol>
          <CCol :md="4">
            <CFormLabel for="job-branch">{{ t('預設分支') }}</CFormLabel>
            <CFormInput id="job-branch" v-model="form.branch" />
          </CCol>
          <CCol :md="6">
            <CFormLabel for="job-description">{{ t('任務說明') }}</CFormLabel>
            <CFormInput
              id="job-description"
              v-model="form.description"
              maxlength="160"
              :placeholder="t('可選，用一句話說明此任務的目的')"
            />
          </CCol>
          <CCol :md="6">
            <CFormLabel for="job-repository">{{ t('原始碼儲存庫') }}</CFormLabel>
            <CFormInput id="job-repository" v-model="form.repository" placeholder="https://github.com/org/repository" />
          </CCol>
        </CRow>
      </CCardBody>

      <CCardBody v-show="tab === 'script'">
        <div class="d-flex flex-wrap align-items-center gap-2 mb-3">
          <span class="text-body-secondary small me-auto">{{ t('流程會編譯成一段腳本存起來，執行器不需要知道動作的存在。') }}</span>

      <CButtonGroup role="group" :aria-label="t('腳本模式')">
        <CButton
          :color="form.scriptMode === 'actions' ? 'primary' : 'secondary'"
          :variant="form.scriptMode === 'actions' ? undefined : 'outline'"
          size="sm"
          @click="setMode('actions')"
        >
          <CIcon icon="cil-layers" class="me-1" />{{ t('流程模式') }}
        </CButton>
        <CButton
          :color="form.scriptMode === 'source' ? 'primary' : 'secondary'"
          :variant="form.scriptMode === 'source' ? undefined : 'outline'"
          size="sm"
          @click="setMode('source')"
        >
          <CIcon icon="cil-code" class="me-1" />{{ t('腳本模式') }}
        </CButton>
      </CButtonGroup>

      <CButton color="secondary" variant="outline" size="sm" @click="reviewScript">
        <CIcon icon="cil-lightbulb" class="me-1" />{{ t('AI 檢查腳本') }}
      </CButton>
        </div>

        <CAlert v-if="fieldError('actions')" color="danger" class="py-2">{{ fieldError('actions') }}</CAlert>

        <template v-if="form.scriptMode === 'actions'">
          <CAlert v-if="switchWarning" color="info" class="py-2">
            {{ t('已切回流程模式，腳本模式下的手動修改不會保留。') }}
          </CAlert>
          <ActionFlowEditor v-model="form.actions" />
        </template>

        <template v-else>
          <div class="d-flex align-items-center justify-content-between mb-2">
            <span class="text-body-secondary small">
              {{ t('直接編輯要執行的 shell 腳本；切回流程模式會還原成原本的步驟。') }}
            </span>
            <CButton color="link" size="sm" class="p-0" @click="openCommandEditor">
              <CIcon icon="cil-fullscreen" class="me-1" />{{ t('全螢幕編輯') }}
            </CButton>
          </div>
          <CFormTextarea
            id="job-command"
            v-model="form.command"
            class="font-monospace"
            rows="14"
            spellcheck="false"
            :invalid="Boolean(fieldError('command'))"
            placeholder="npm ci&#10;npm run build"
          />
          <CFormFeedback invalid>{{ fieldError('command') }}</CFormFeedback>
        </template>
      </CCardBody>

      <CCardBody v-show="tab === 'build'">
        <p class="text-body-secondary">{{ t('命令會由指定的執行器在工作目錄中執行。') }}</p>
        <CRow class="g-3">
          <CCol :md="6">
            <CFormLabel for="job-runner">{{ t('執行器') }}</CFormLabel>
            <CFormSelect id="job-runner" v-model="form.runnerName">
              <option v-for="runner in runners.items" :key="runner.id" :value="runner.name">
                {{ runner.name }}
              </option>
            </CFormSelect>
          </CCol>
          <CCol :md="6">
            <CFormLabel for="job-directory">{{ t('工作目錄') }}</CFormLabel>
            <CFormInput
              id="job-directory"
              v-model="form.workingDirectory"
              class="font-monospace"
              :placeholder="settings.settings?.defaultWorkingDirectory ?? '/workspace/project'"
            />
          </CCol>
          <CCol :md="6">
            <CFormLabel for="job-timeout">{{ t('逾時時間（分鐘）') }}</CFormLabel>
            <CFormInput
              id="job-timeout"
              v-model.number="form.timeoutMinutes"
              type="number"
              min="1"
              max="1440"
              :invalid="Boolean(fieldError('timeoutMinutes'))"
            />
            <CFormFeedback invalid>{{ fieldError('timeoutMinutes') }}</CFormFeedback>
          </CCol>
          <CCol :md="6">
            <CFormLabel for="job-artifact">{{ t('產出檔案路徑') }}</CFormLabel>
            <CFormInput id="job-artifact" v-model="form.artifactPath" class="font-monospace" placeholder="dist/" />
          </CCol>
          <CCol :xs="12">
            <CFormLabel for="job-cache">{{ t('建置快取') }}</CFormLabel>
            <CFormTextarea
              id="job-cache"
              v-model="form.cachePaths"
              class="font-monospace"
              rows="2"
              placeholder="node_modules"
            />
            <CFormText>
              {{ t('一行一個相對於工作目錄的路徑，成功的建置會保存下來給下一次還原；絕對路徑與 .. 會被忽略。') }}
            </CFormText>
          </CCol>
          <CCol :xs="12">
            <CFormLabel for="job-clean">{{ t('建置前清空工作目錄') }}</CFormLabel>
            <CFormSelect id="job-clean" v-model="form.cleanWorkspace">
              <option value="">{{ t('跟隨系統設定') }}</option>
              <option value="true">{{ t('每次建置前清空') }}</option>
              <option value="false">{{ t('保留上一次的內容') }}</option>
            </CFormSelect>
            <CFormText>
              {{ t('只清空目錄裡的內容，不會刪掉目錄本身；清空在還原建置快取之前執行。未指定時跟隨設定頁的「每次建置前清空工作目錄」。') }}
            </CFormText>
          </CCol>
          <CCol :xs="12">
            <CFormLabel for="job-clean-after">{{ t('建置後清空工作目錄') }}</CFormLabel>
            <CFormSelect id="job-clean-after" v-model="form.cleanWorkspaceAfterBuild">
              <option value="">{{ t('跟隨系統設定') }}</option>
              <option value="true">{{ t('每次建置後清空') }}</option>
              <option value="false">{{ t('建置後保留內容') }}</option>
            </CFormSelect>
            <CFormText>
              {{ t('建置結束後（不論成功或失敗）清空工作目錄，產出檔案與快取會先保存；未指定時跟隨設定頁的「每次建置後清空工作目錄」。') }}
            </CFormText>
            <CFormText v-if="suggestCleanAfterBuild" class="d-block text-warning">
              {{ t('這個任務有密碼參數或任務專屬機密，建議開啟建置後清空，避免殘留在工作目錄。') }}
            </CFormText>
          </CCol>
          <CCol :xs="12">
            <CFormLabel for="job-matrix">{{ t('矩陣') }}</CFormLabel>
            <CFormTextarea
              id="job-matrix"
              v-model="form.matrixJson"
              class="font-monospace"
              rows="2"
              :invalid="Boolean(errors.matrixJson)"
              placeholder='{"NODE":["20","22"],"OS":["mac","linux"]}'
            />
            <CFormFeedback invalid>{{ errors.matrixJson?.[0] }}</CFormFeedback>
            <CFormText>
              {{ t('設定後，一次觸發會為每個組合各跑一次建置，值以環境變數注入；上限 50 組。') }}
            </CFormText>
          </CCol>
          <CCol :xs="12">
            <CFormLabel>{{ t('建置參數') }}</CFormLabel>
            <BuildParametersEditor v-model="form.parametersJson" />
            <CFormFeedback v-if="errors.parametersJson" class="d-block" invalid>{{ errors.parametersJson?.[0] }}</CFormFeedback>
            <CFormText>
              {{ t('手動執行時會先填這些值，排程與推送用預設值；腳本以 $名稱 讀取，密碼型別會在日誌裡遮蔽。') }}
            </CFormText>
          </CCol>
          <CCol :xs="12">
            <div class="border rounded p-3">
              <CFormSwitch
                id="job-serial"
                :model-value="!form.allowConcurrentBuilds"
                :label="t('同一時間只跑一筆建置')"
                @update:model-value="(value: boolean) => (form.allowConcurrentBuilds = !value)"
              />
              <div class="small text-body-secondary ms-4">{{ t('前一筆還在執行時，新的建置留在佇列等它結束再開始。') }}</div>
            </div>
          </CCol>
          <CCol :xs="12">
            <div class="border rounded p-3">
              <CFormSwitch id="job-deny-network" v-model="form.denyNetwork" :label="t('拒絕網路')" />
              <div class="small text-body-secondary ms-4 mb-3">
                {{ t('有沙箱的執行器（macOS 內建執行器）會擋住建置的所有網路連線，連 git fetch 與套件下載也不行；沒有沙箱的執行器不受影響。') }}
              </div>
              <CFormSwitch
                id="job-trusted"
                v-model="form.trustedUnsandboxed"
                :disabled="!auth.isAdmin"
                :label="t('信任任務：不套用沙箱（只有管理員能開）')"
              />
              <div class="small text-body-secondary ms-4">
                {{ t('腳本以服務帳號的完整權限直接執行，讀寫得到 Walis 的資料目錄、安裝目錄與服務帳號的家目錄；也不受「沒有隔離邊界時拒絕執行」限制。只給自我更新這類確實需要寫安裝目錄的任務，建置日誌第一行會註明。') }}
              </div>
              <CAlert v-if="form.trustedUnsandboxed" color="warning" class="mt-2 mb-0 py-2 small ms-4">
                {{ t('任何能編輯這個任務的人都能以服務帳號執行任意程式；請把「可以執行這個任務的角色」設為僅管理員。') }}
              </CAlert>
            </div>
          </CCol>
          <CCol :md="6">
            <CFormLabel for="job-view-role">{{ t('可以看到這個任務的角色') }}</CFormLabel>
            <CFormSelect id="job-view-role" v-model="form.viewRole">
              <option value="viewer">{{ t('所有人') }}</option>
              <option value="operator">{{ t('操作者以上') }}</option>
              <option value="admin">{{ t('僅管理員') }}</option>
            </CFormSelect>
          </CCol>
          <CCol :md="6">
            <CFormLabel for="job-run-role">{{ t('可以執行這個任務的角色') }}</CFormLabel>
            <CFormSelect id="job-run-role" v-model="form.runRole">
              <option value="operator">{{ t('操作者以上') }}</option>
              <option value="admin">{{ t('僅管理員') }}</option>
            </CFormSelect>
            <CFormText>{{ t('看不到的任務連識別碼都拿不到，直接查會回 404。') }}</CFormText>
          </CCol>
        </CRow>
        <hr class="my-4" />
        <div class="d-flex flex-wrap align-items-center gap-2">
          <span class="text-body-secondary small me-auto">{{ t('建置執行時會注入到 shell 環境') }}</span>
          <CButton color="secondary" variant="outline" size="sm" @click="router.push('/environment')">
            <CIcon icon="cil-code" class="me-1" />{{ t('管理環境變數') }}
          </CButton>
        </div>
      </CCardBody>

      <CCardBody v-show="tab === 'trigger'">
        <p class="text-body-secondary">{{ t('可同時啟用手動、程式碼變更與固定排程觸發。') }}</p>
        <CRow class="g-3">
          <CCol :md="6">
            <div class="border rounded p-3 h-100">
              <CFormCheck
                id="trigger-manual"
                type="radio"
                name="trigger"
                :model-value="form.triggerKind === 'manual'"
                :label="t('允許手動執行')"
                @change="form.triggerKind = 'manual' as TriggerKind"
              />
              <div class="small text-body-secondary ms-4">{{ t('在任務清單與詳情頁提供立即執行。') }}</div>
            </div>
          </CCol>
          <CCol :md="6">
            <div class="border rounded p-3 h-100">
              <CFormCheck
                id="trigger-push"
                type="radio"
                name="trigger"
                :model-value="form.triggerKind === 'push'"
                :label="t('推送後自動建置')"
                @change="form.triggerKind = 'push' as TriggerKind"
              />
              <div class="small text-body-secondary ms-4">
                {{ t('指定分支有新提交時自動建立建置。') }}
                {{ t('（推送 webhook 網址在系統設定的安全分頁。）') }}
              </div>
            </div>
          </CCol>
          <CCol :xs="12">
            <div class="border rounded p-3">
              <CFormCheck
                id="trigger-cron"
                type="radio"
                name="trigger"
                :model-value="form.triggerKind === 'cron'"
                :label="t('固定排程')"
                @change="form.triggerKind = 'cron' as TriggerKind"
              />
              <div class="small text-body-secondary ms-4">{{ t('以 Cron 排程於指定時間自動執行。') }}</div>

              <template v-if="form.triggerKind === 'cron'">
                <CFormInput
                  v-model="form.cron"
                  class="font-monospace mt-3"
                  placeholder="30 2 * * *"
                  :invalid="Boolean(fieldError('cron'))"
                />
                <CFormFeedback invalid>{{ fieldError('cron') }}</CFormFeedback>
                <CFormText>{{ t('格式：分 時 日 月 週。') }}</CFormText>
                <div class="d-flex flex-wrap gap-2 mt-2">
                  <CButton
                    v-for="preset in cronPresets"
                    :key="preset.value"
                    color="secondary"
                    variant="outline"
                    size="sm"
                    @click="form.cron = preset.value"
                  >
                    {{ t(preset.label) }}
                  </CButton>
                </div>
              </template>
            </div>
          </CCol>
          <CCol :md="6">
            <div class="border rounded p-3 h-100">
              <CFormCheck id="notify-failure" v-model="form.notifyOnFailure" :label="t('失敗時通知')" />
              <div class="small text-body-secondary ms-4">{{ t('建置失敗時發送通知到設定的頻道。') }}</div>
            </div>
          </CCol>
          <CCol :xs="12">
            <div class="border rounded p-3">
              <CFormLabel class="mb-1">{{ t('完成後觸發下游任務') }}</CFormLabel>
              <div class="small text-body-secondary mb-2">
                {{ t('依這筆建置的結果觸發其他任務；下游拿得到 WALIS_UPSTREAM_JOB、WALIS_UPSTREAM_BUILD 與 WALIS_UPSTREAM_STATUS。') }}
              </div>
              <DownstreamEditor v-model="form.downstreamJson" :own-name="form.name" />
              <CFormFeedback v-if="errors.downstreamJson" class="d-block" invalid>{{ errors.downstreamJson?.[0] }}</CFormFeedback>
            </div>
          </CCol>
          <CCol :md="6">
            <div class="border rounded p-3 h-100">
              <CFormCheck id="job-disabled" v-model="form.isDisabled" :label="t('建立後先暫停')" />
              <div class="small text-body-secondary ms-4">{{ t('暫停中的任務不會被執行，也不會依排程觸發。') }}</div>
            </div>
          </CCol>
        </CRow>
      </CCardBody>
    </CCard>

    <!-- Cancel/save sit to the right of the breadcrumb row in the header, so no scrolling to the bottom. The buttons are outside the form, hence click instead of submit. -->
    <Teleport to="#page-actions">
      <span class="text-body-secondary small d-none d-md-inline">{{ t('離開此頁不會儲存修改。') }}</span>
      <CButton color="secondary" variant="outline" size="sm" :disabled="saving" @click="router.push('/jobs')">
        {{ t('取消') }}
      </CButton>
      <CButton color="primary" size="sm" :disabled="saving" @click="submit">
        <CSpinner v-if="saving" size="sm" class="me-2" />
        {{ t(isEdit ? '儲存任務' : '建立任務') }}
      </CButton>
    </Teleport>
  </CForm>

  <CModal size="xl" scrollable :visible="commandExpanded" @close="commandExpanded = false">
    <CModalHeader>
      <div>
        <CModalTitle>{{ t('編輯執行命令') }}</CModalTitle>
        <div class="small text-body-secondary">{{ t('適合貼上或調整多行建置腳本。') }}</div>
      </div>
    </CModalHeader>
    <CModalBody>
      <CFormTextarea v-model="expandedCommand" class="font-monospace" rows="18" spellcheck="false" />
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="commandExpanded = false">{{ t('取消') }}</CButton>
      <CButton color="primary" @click="applyCommand">{{ t('套用命令') }}</CButton>
    </CModalFooter>
  </CModal>

  <AiAnswerModal
    :visible="aiVisible"
    :title="t('AI 腳本檢查')"
    :answer="aiAnswer"
    :loading="aiLoading"
    :error="aiError"
    @close="aiVisible = false"
    @retry="reviewScript"
  />
</template>
