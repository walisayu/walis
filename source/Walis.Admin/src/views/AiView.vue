<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import { usePagedRows } from '../composables/usePagedRows'
import PageTabs from '../components/PageTabs.vue'
import PageActions from '../components/PageActions.vue'
import { ApiError } from '../services/api'
import { useFormat } from '../composables/useFormat'
import { useAiStore } from '../stores/ai'
import { useToastStore } from '../stores/toasts'
import type { AiProvider } from '../types'

const { t } = useI18n()

    const tab = ref<'providers' | 'log'>('providers')
    const tabs = [
      { key: 'providers', label: '供應商', icon: 'cil-lightbulb' },
      { key: 'log', label: 'API 呼叫紀錄', icon: 'cil-list-rich' }
    ]
    const ai = useAiStore()
const toasts = useToastStore()
const { dateTime, relative } = useFormat()

interface Draft {
  apiKey: string
  clearKey: boolean
  model: string
  baseUrl: string
  isEnabled: boolean
  isDefault: boolean
}

const drafts = reactive<Record<string, Draft>>({})
const saving = ref<string | null>(null)
const testing = ref<string | null>(null)
const errors = ref<Record<string, Record<string, string[]>>>({})

const purposeLabels: Record<string, string> = {
  'analyze-build': '分析建置',
  'review-script': '審查腳本',
  test: '測試連線'
}

const providerHints: Record<string, string> = {
  gemini: '在 Google AI Studio 建立 API 金鑰。',
  claude: '在 Anthropic Console 建立 API 金鑰。',
  codex: '在 OpenAI Platform 建立 API 金鑰。'
}

watch(
  () => ai.providers,
  (providers) => {
    for (const provider of providers) {
      drafts[provider.id] = {
        apiKey: '',
        clearKey: false,
        model: provider.model,
        baseUrl: provider.baseUrl ?? '',
        isEnabled: provider.isEnabled,
        isDefault: provider.isDefault
      }
    }
  },
  { immediate: true, deep: true }
)

async function save(provider: AiProvider): Promise<void> {
  const draft = drafts[provider.id]

  if (!draft) {
    return
  }

  saving.value = provider.id
  errors.value = { ...errors.value, [provider.id]: {} }

  try {
    await ai.update(provider.id, {
      // Sending null when empty keeps the existing key; an empty string is sent only when clear is checked.
      apiKey: draft.clearKey ? '' : draft.apiKey.trim() || null,
      model: draft.model.trim(),
      baseUrl: draft.baseUrl.trim() || null,
      isEnabled: draft.isEnabled,
      isDefault: draft.isDefault
    })

    draft.apiKey = ''
    draft.clearKey = false
    toasts.success(t('{name} 的設定已儲存', { name: provider.name }))
  } catch (exception) {
    if (exception instanceof ApiError) {
      errors.value = { ...errors.value, [provider.id]: exception.errors }
      toasts.error(exception.message)
    } else {
      toasts.error(exception instanceof Error ? exception.message : String(exception))
    }
  } finally {
    saving.value = null
  }
}

async function test(provider: AiProvider): Promise<void> {
  testing.value = provider.id

  try {
    const text = await ai.test(provider.id)
    toasts.success(text.trim().slice(0, 120) || t('連線正常'))
  } catch (exception) {
    toasts.error(exception instanceof Error ? exception.message : String(exception))
  } finally {
    testing.value = null
  }
}

function fieldError(providerId: string, field: string): string | undefined {
  return errors.value[providerId]?.[field]?.[0]
}

onMounted(async () => {
  await ai.load(true)
  await ai.loadRequests().catch(() => undefined)
})

const requestPage = usePagedRows(() => ai.requests)
</script>

<template>
  <CAlert v-if="!ai.loading && !ai.isReady" color="info" class="d-flex align-items-center">
    <CIcon icon="cil-info" class="flex-shrink-0 me-2" />
    <span>{{ t('填入任一家的 API 金鑰並啟用後，就可以在建置輸出與任務編輯頁使用 AI 分析。') }}</span>
  </CAlert>

    <PageActions>
      <CButton color="secondary" variant="outline" size="sm" @click="ai.loadRequests()">
        <CIcon icon="cil-reload" class="me-1" />{{ t('重新整理') }}
      </CButton>
    </PageActions>

      <PageTabs v-model="tab" :tabs="tabs">
        <template #providers>
          <CCardBody>
      <CRow class="g-4 mb-4">
    <CCol v-for="provider in ai.providers" :key="provider.id" :md="6" :xl="4">
      <CCard class="h-100">
        <CCardHeader class="d-flex align-items-center gap-2">
          <span class="me-auto fw-semibold">{{ provider.name }}</span>
          <CBadge v-if="provider.isDefault" text-bg-color="primary">{{ t('預設') }}</CBadge>
          <CBadge :text-bg-color="provider.isUsable ? 'success' : 'secondary'">
            {{ t(provider.isUsable ? '可使用' : '未啟用') }}
          </CBadge>
        </CCardHeader>

        <CCardBody v-if="drafts[provider.id]">
          <p class="small text-body-secondary">{{ t(providerHints[provider.kind] ?? '') }}</p>

          <div class="mb-3">
            <CFormLabel :for="`key-${provider.id}`">{{ t('API 金鑰') }}</CFormLabel>
            <CFormInput
              :id="`key-${provider.id}`"
              v-model="drafts[provider.id].apiKey"
              type="password"
              autocomplete="new-password"
              :placeholder="provider.hasApiKey ? t('已設定，留空表示不變更') : t('尚未設定')"
              :disabled="drafts[provider.id].clearKey"
            />
            <CFormCheck
              v-if="provider.hasApiKey"
              :id="`clear-${provider.id}`"
              v-model="drafts[provider.id].clearKey"
              class="mt-2"
              :label="t('清除已儲存的金鑰')"
            />
          </div>

          <div class="mb-3">
            <CFormLabel :for="`model-${provider.id}`">{{ t('模型') }}</CFormLabel>
            <CFormInput
              :id="`model-${provider.id}`"
              v-model="drafts[provider.id].model"
              class="font-monospace"
              :invalid="Boolean(fieldError(provider.id, 'model'))"
            />
            <CFormFeedback invalid>{{ fieldError(provider.id, 'model') }}</CFormFeedback>
            <CFormText>{{ t('預設為 {model}，可改成你有權限的型號。', { model: provider.defaultModel }) }}</CFormText>
          </div>

          <div class="mb-3">
            <CFormLabel :for="`base-${provider.id}`">{{ t('自訂端點') }}</CFormLabel>
            <CFormInput
              :id="`base-${provider.id}`"
              v-model="drafts[provider.id].baseUrl"
              class="font-monospace"
              :placeholder="t('留空使用官方端點')"
              :invalid="Boolean(fieldError(provider.id, 'baseUrl'))"
            />
            <CFormFeedback invalid>{{ fieldError(provider.id, 'baseUrl') }}</CFormFeedback>
          </div>

          <CFormSwitch
            :id="`enabled-${provider.id}`"
            v-model="drafts[provider.id].isEnabled"
            class="mb-2"
            :label="t('啟用')"
          />
          <CFormSwitch
            :id="`default-${provider.id}`"
            v-model="drafts[provider.id].isDefault"
            :label="t('設為預設供應商')"
          />
        </CCardBody>

        <CCardFooter class="d-flex gap-2">
          <CButton
            color="secondary"
            variant="outline"
            size="sm"
            :disabled="testing === provider.id || !provider.hasApiKey"
            @click="test(provider)"
          >
            <CSpinner v-if="testing === provider.id" size="sm" class="me-1" />{{ t('測試') }}
          </CButton>
          <CButton color="primary" size="sm" class="ms-auto" :disabled="saving === provider.id" @click="save(provider)">
            <CSpinner v-if="saving === provider.id" size="sm" class="me-1" />{{ t('儲存') }}
          </CButton>
        </CCardFooter>
      </CCard>
    </CCol>
  </CRow>
      </CCardBody>
        </template>
        <template #log>
        <CCardBody class="p-0">
      <div class="table-responsive">
        <CTable align="middle" class="mb-0">
          <CTableHead class="text-nowrap">
            <CTableRow>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('時間') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('供應商') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('模型') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('用途') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('任務') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('操作者') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('用量') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('結果') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('錯誤') }}</CTableHeaderCell>
            </CTableRow>
          </CTableHead>
          <CTableBody>
            <CTableRow v-for="entry in requestPage.paged" :key="entry.id">
              <CTableDataCell class="text-body-secondary text-nowrap" :title="dateTime(entry.startedAt)">
                {{ relative(entry.startedAt) }}
              </CTableDataCell>
              <CTableDataCell class="fw-semibold">{{ entry.providerKind }}</CTableDataCell>
              <CTableDataCell class="font-monospace small text-body-secondary">{{ entry.model }}</CTableDataCell>
              <CTableDataCell>{{ t(purposeLabels[entry.purpose] ?? entry.purpose) }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary">{{ entry.jobName ?? '—' }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary">{{ entry.actor }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary small text-nowrap">
                {{ t('{ms} 毫秒', { ms: entry.durationMs }) }}
                <template v-if="entry.inputTokens !== null">
                  · {{ t('輸入 {input} / 輸出 {output} tokens', { input: entry.inputTokens, output: entry.outputTokens ?? 0 }) }}
                </template>
              </CTableDataCell>
              <CTableDataCell>
                <CBadge :text-bg-color="entry.succeeded ? 'success' : 'danger'">
                  {{ t(entry.succeeded ? '成功' : '失敗') }}
                </CBadge>
              </CTableDataCell>
              <CTableDataCell class="small text-body-secondary text-truncate" style="max-width: 20rem" :title="entry.error ?? ''">
                {{ entry.error ?? '—' }}
              </CTableDataCell>
            </CTableRow>

            <CTableRow v-if="ai.requests.length === 0">
              <CTableDataCell colspan="9" class="text-center text-body-secondary py-5">
                {{ t('尚未有 AI 呼叫紀錄。') }}
              </CTableDataCell>
            </CTableRow>
          </CTableBody>
        </CTable>
      </div>
    </CCardBody>
    <TablePagination
      :page="requestPage.page"
      :page-size="requestPage.pageSize"
      :total="requestPage.total"
      :pages="requestPage.pages"
      @update:page="requestPage.setPage"
      @update:page-size="requestPage.setPageSize"
    />
    <CCardFooter class="text-body-secondary small">
      {{ t('紀錄只保存呼叫的統計資訊，不會保存送出的提示詞內容。') }}
    </CCardFooter>
    </template>
      </PageTabs>
    </template>
