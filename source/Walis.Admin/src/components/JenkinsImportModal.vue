<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ApiError, api } from '../services/api'
import { useImportDraftStore } from '../stores/importDraft'
import { useToastStore } from '../stores/toasts'
import { parseParameterDefinitions } from '../utils/buildParameters'
import type { JenkinsImportResult } from '../types'

const props = defineProps<{ visible: boolean }>()
const emit = defineEmits<{ (event: 'close'): void }>()

const { t } = useI18n()
const router = useRouter()
const draft = useImportDraftStore()
const toasts = useToastStore()

const content = ref('')
const name = ref('')
const loading = ref(false)
const result = ref<JenkinsImportResult | null>(null)
const error = ref('')

watch(
  () => props.visible,
  (visible) => {
    if (visible) {
      content.value = ''
      name.value = ''
      result.value = null
      error.value = ''
    }
  }
)

const triggerLabels: Record<string, string> = { manual: '手動', push: '推送後自動', cron: '固定排程' }
const severityColor: Record<string, string> = { info: 'info', warning: 'warning', unsupported: 'danger' }
const severityLabel: Record<string, string> = { info: '提示', warning: '需要處理', unsupported: '不支援' }

const parameters = computed(() => parseParameterDefinitions(result.value?.job ?? null))
const warningCounts = computed(() => {
  const counts = { info: 0, warning: 0, unsupported: 0 }
  for (const warning of result.value?.warnings ?? []) {
    counts[warning.severity] += 1
  }
  return counts
})

async function pickFile(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) {
    return
  }

  content.value = await file.text()
  if (!name.value && file.name !== 'Jenkinsfile' && file.name !== 'config.xml') {
    name.value = file.name.replace(/\.(groovy|jenkinsfile|xml)$/i, '')
  }

  input.value = ''
}

async function preview(): Promise<void> {
  loading.value = true
  error.value = ''
  result.value = null

  try {
    result.value = await api.importJenkins(content.value, name.value || null)
  } catch (exception) {
    error.value = exception instanceof ApiError ? exception.message : String(exception)
  } finally {
    loading.value = false
  }
}

async function openInEditor(): Promise<void> {
  if (!result.value) {
    return
  }

  draft.set({ ...result.value.job, name: name.value || result.value.job.name }, result.value.environment)
  emit('close')
  toasts.info(t('已把匯入結果帶進編輯器，確認後儲存即可建立任務。'))
  await router.push('/jobs/new?from=jenkins')
}
</script>

<template>
  <CModal size="xl" scrollable :visible="visible" @close="emit('close')">
    <CModalHeader>
      <CModalTitle>{{ t('從 Jenkins 匯入') }}</CModalTitle>
    </CModalHeader>
    <CModalBody>
      <template v-if="!result">
        <p class="text-body-secondary small">
          {{ t('貼上 Jenkinsfile（宣告式或腳本式）或任務的 config.xml。stage 會變成步驟、parallel 變成平行步驟、post 變成執行條件、parameters 變成建置參數；對不上的會列成警告並留成註解。') }}
        </p>
        <CRow class="g-3">
          <CCol :md="8">
            <CFormLabel for="jenkins-name">{{ t('任務名稱') }}</CFormLabel>
            <CFormInput id="jenkins-name" v-model="name" :placeholder="t('留空則在編輯器再填')" />
          </CCol>
          <CCol :md="4" class="d-flex align-items-end">
            <CFormInput type="file" accept=".xml,.groovy,Jenkinsfile,*" @change="pickFile" />
          </CCol>
          <CCol :xs="12">
            <CFormTextarea
              v-model="content"
              class="font-monospace"
              rows="16"
              placeholder="pipeline {&#10;  agent any&#10;  stages {&#10;    stage('Build') { steps { sh 'npm ci' } }&#10;  }&#10;}"
            />
          </CCol>
        </CRow>
        <CAlert v-if="error" color="danger" class="mt-3 mb-0">{{ error }}</CAlert>
      </template>

      <template v-else>
        <div class="d-flex flex-wrap align-items-center gap-2 mb-3">
          <CBadge color="secondary">{{ result.format }}</CBadge>
          <span class="fw-semibold">{{ name || result.job.name || t('（尚未命名）') }}</span>
          <span class="text-body-secondary small">
            · {{ t(triggerLabels[result.job.triggerKind] ?? result.job.triggerKind) }}
            <code v-if="result.job.cron" class="ms-1">{{ result.job.cron }}</code>
            · {{ t('逾時 {minutes} 分鐘', { minutes: result.job.timeoutMinutes }) }}
            <template v-if="!result.job.allowConcurrentBuilds"> · {{ t('同一時間只跑一筆建置') }}</template>
          </span>
          <span class="ms-auto small">
            <CBadge v-if="warningCounts.unsupported" color="danger" class="me-1">{{ t('不支援 {count}', { count: warningCounts.unsupported }) }}</CBadge>
            <CBadge v-if="warningCounts.warning" color="warning" text-color="dark" class="me-1">{{ t('需要處理 {count}', { count: warningCounts.warning }) }}</CBadge>
            <CBadge v-if="warningCounts.info" color="info">{{ t('提示 {count}', { count: warningCounts.info }) }}</CBadge>
          </span>
        </div>

        <CRow class="g-3">
          <CCol :lg="7">
            <h6>{{ t('步驟') }} <span class="text-body-secondary small">{{ result.job.actions.length }}</span></h6>
            <div v-for="(step, index) in result.job.actions" :key="step.id" class="border rounded p-2 mb-2">
              <div class="d-flex align-items-center gap-2">
                <span class="badge text-bg-light border">{{ index + 1 }}</span>
                <span class="fw-semibold">{{ step.name }}</span>
                <CBadge v-if="step.actionId === 'container.run'" color="light" class="border text-body">{{ step.parameters.image }}</CBadge>
                <CBadge v-if="step.parallel" color="light" class="border text-body">{{ t('平行') }}</CBadge>
                <CBadge v-if="step.runWhen === 'always'" color="light" class="border text-body">{{ t('總是') }}</CBadge>
                <CBadge v-if="step.runWhen === 'failure'" color="light" class="border text-body">{{ t('失敗時') }}</CBadge>
                <CBadge v-if="step.retries" color="light" class="border text-body">{{ t('重試 {count} 次', { count: step.retries }) }}</CBadge>
              </div>
              <pre class="small bg-body-tertiary rounded p-2 mt-2 mb-0 import-script"><code>{{ step.parameters.command ?? step.parameters.script }}</code></pre>
            </div>
            <p v-if="result.job.actions.length === 0" class="text-body-secondary">{{ t('沒有找到可以執行的步驟。') }}</p>
          </CCol>
          <CCol :lg="5">
            <template v-if="parameters.length">
              <h6>{{ t('建置參數') }}</h6>
              <ul class="small mb-3">
                <li v-for="parameter in parameters" :key="parameter.name">
                  <code>{{ parameter.name }}</code> · {{ parameter.type }}
                  <span v-if="parameter.default" class="text-body-secondary">= {{ parameter.default }}</span>
                </li>
              </ul>
            </template>
            <template v-if="result.environment.length">
              <h6>{{ t('環境變數') }}</h6>
              <ul class="small mb-3">
                <li v-for="variable in result.environment" :key="variable.name">
                  <code>{{ variable.name }}</code>
                  <CBadge v-if="variable.isSecret" color="warning" text-color="dark" class="ms-1">{{ t('機密，待填') }}</CBadge>
                  <span v-else class="text-body-secondary">= {{ variable.value }}</span>
                  <span v-if="variable.source" class="text-body-secondary"> · {{ variable.source }}</span>
                </li>
              </ul>
            </template>
            <template v-if="result.job.downstreamJson">
              <h6>{{ t('下游任務') }}</h6>
              <p class="small"><code>{{ result.job.downstreamJson }}</code></p>
            </template>
            <template v-if="result.job.artifactPath">
              <h6>{{ t('產出檔案路徑') }}</h6>
              <pre class="small mb-3">{{ result.job.artifactPath }}</pre>
            </template>

            <h6>{{ t('警告') }} <span class="text-body-secondary small">{{ result.warnings.length }}</span></h6>
            <p v-if="result.warnings.length === 0" class="text-body-secondary small">{{ t('全部對得上，沒有需要注意的地方。') }}</p>
            <div v-for="(warning, index) in result.warnings" :key="index" class="small mb-2">
              <CBadge :color="severityColor[warning.severity]" :text-color="warning.severity === 'warning' ? 'dark' : undefined" class="me-1">
                {{ t(severityLabel[warning.severity] ?? warning.severity) }}
              </CBadge>
              <span v-if="warning.line" class="text-body-secondary">L{{ warning.line }} </span>
              {{ warning.message }}
              <pre v-if="warning.snippet" class="text-body-secondary mb-0 mt-1 import-script">{{ warning.snippet }}</pre>
            </div>
          </CCol>
        </CRow>
      </template>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="emit('close')">{{ t('取消') }}</CButton>
      <template v-if="!result">
        <CButton color="primary" :disabled="loading || !content.trim()" @click="preview">
          <CSpinner v-if="loading" size="sm" class="me-2" />{{ t('預覽') }}
        </CButton>
      </template>
      <template v-else>
        <CButton color="secondary" variant="outline" @click="result = null">{{ t('回上一步') }}</CButton>
        <CButton color="primary" :disabled="result.job.actions.length === 0" @click="openInEditor">
          {{ t('在編輯器中開啟') }}
        </CButton>
      </template>
    </CModalFooter>
  </CModal>
</template>

<style scoped>
.import-script {
  max-height: 12rem;
  overflow: auto;
  white-space: pre-wrap;
}
</style>
