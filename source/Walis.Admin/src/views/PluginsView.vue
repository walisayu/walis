<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import { usePagedRows } from '../composables/usePagedRows'
import { ApiError, api } from '../services/api'
import { useFormat } from '../composables/useFormat'
import { useToastStore } from '../stores/toasts'
import PageActions from '../components/PageActions.vue'
import MarkdownText from '../components/MarkdownText.vue'
import { developmentGuide } from '../content/pluginGuide'
import type { PluginSummary } from '../types'

const { t } = useI18n()
const { dateTime } = useFormat()
const toasts = useToastStore()

const plugins = ref<PluginSummary[]>([])
const loading = ref(false)
const activeTab = ref<'installed' | 'guide'>('installed')

const requirement = ref('')
const generating = ref(false)
const warnings = ref<string[]>([])

const editing = ref<{ manifest: string; code: string; isTrusted: boolean } | null>(null)
const preview = ref<{ ok: boolean; text: string } | null>(null)
const saving = ref(false)

const sample = `{
  "id": "acme.deploy",
  "name": "部署到測試機",
  "description": "呼叫團隊的部署腳本。",
  "version": "1.0.0",
  "apiVersion": 1,
  "kind": "action",
  "category": "deploy",
  "icon": "cil-cloud-upload",
  "parameters": [
    { "name": "target", "label": "目標主機", "required": true }
  ],
  "permissions": { "http": [], "secrets": [], "kv": false }
}`

const sampleCode = `// render returns a piece of shell that Walis runs as one build step.
// Always wrap values with ctx.shell.quote; the escaping rules match action templates.
function render(inputs, ctx) {
  return 'scripts/deploy.sh ' + ctx.shell.quote(inputs.target)
}`

async function load(): Promise<void> {
  loading.value = true

  try {
    plugins.value = await api.plugins()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    loading.value = false
  }
}

function openEditor(): void {
  preview.value = null
  warnings.value = []
  requirement.value = ''
  editing.value = { manifest: sample, code: sampleCode, isTrusted: false }
}

function parseManifest(): unknown {
  return JSON.parse(editing.value!.manifest)
}

/**
 * Generates a draft with AI. Deliberately only fills the form and never installs directly:
 * the model's output is as untrusted as the requirement the user pasted in, so a person must review and preview before installing.
 */
async function generate(): Promise<void> {
  if (!editing.value || !requirement.value.trim()) {
    return
  }

  generating.value = true
  warnings.value = []
  preview.value = null

  try {
    const result = await api.generatePlugin(requirement.value)

    if (!result.ok) {
      preview.value = { ok: false, text: `${result.message}\n\n${result.raw ?? ''}` }
      return
    }

    editing.value.manifest = JSON.stringify(result.manifest, null, 2)
    editing.value.code = result.code ?? ''
    editing.value.isTrusted = false
    warnings.value = result.warnings ?? []
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    generating.value = false
  }
}

async function runPreview(): Promise<void> {
  if (!editing.value) {
    return
  }

  try {
    const result = await api.previewPlugin(parseManifest(), editing.value.code, editing.value.isTrusted)
    preview.value = { ok: result.ok, text: result.ok ? result.script : result.message }
  } catch (error) {
    preview.value = {
      ok: false,
      text: error instanceof SyntaxError ? t('manifest 不是有效的 JSON。') : String(error)
    }
  }
}

async function install(): Promise<void> {
  if (!editing.value) {
    return
  }

  saving.value = true

  try {
    await api.installPlugin(parseManifest(), editing.value.code, editing.value.isTrusted)
    editing.value = null
    preview.value = null
    await load()
    toasts.success(t('外掛已安裝'))
  } catch (error) {
    if (error instanceof SyntaxError) {
      toasts.error(t('manifest 不是有效的 JSON。'))
    } else if (error instanceof ApiError) {
      toasts.error(Object.values(error.errors).flat().join('；') || error.message)
    } else {
      toasts.error(String(error))
    }
  } finally {
    saving.value = false
  }
}

async function toggle(plugin: PluginSummary): Promise<void> {
  try {
    await api.setPluginDisabled(plugin.pluginId, !plugin.isDisabled)
    await load()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function remove(plugin: PluginSummary): Promise<void> {
  try {
    await api.uninstallPlugin(plugin.pluginId)
    await load()
    toasts.info(t('已移除外掛 {name}', { name: plugin.manifest.name }))
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

onMounted(load)

const pluginPage = usePagedRows(() => plugins.value)
</script>

<template>
  <CCard class="mb-4">
    <CCardHeader class="d-flex flex-wrap align-items-center gap-2">
      <CNav variant="pills" class="me-auto">
        <CNavItem>
          <button
            class="nav-link"
            type="button"
            :class="{ active: activeTab === 'installed' }"
            @click="activeTab = 'installed'"
          >
            {{ t('已安裝') }}
          </button>
        </CNavItem>
        <CNavItem>
          <button
            class="nav-link"
            type="button"
            :class="{ active: activeTab === 'guide' }"
            @click="activeTab = 'guide'"
          >
            {{ t('開發指南') }}
          </button>
        </CNavItem>
      </CNav>
    </CCardHeader>
    <PageActions>
      <CButton
        v-if="activeTab === 'installed'"
        color="secondary"
        variant="outline"
        size="sm"
        :disabled="loading"
        @click="load"
      >
        <CIcon icon="cil-reload" class="me-1" />{{ t('重新整理') }}
      </CButton>
      <CButton color="primary" size="sm" @click="openEditor">
        <CIcon icon="cil-plus" class="me-1" />{{ t('安裝外掛') }}
      </CButton>
    </PageActions>

    <CCardBody v-if="activeTab === 'guide'">
      <MarkdownText :text="developmentGuide" />
    </CCardBody>

    <CCardBody v-else>
      <CAlert color="info" class="small">
        {{ t('外掛只能回傳一段 shell，實際執行由 Walis 負責——所以安裝前看得到它會產生什麼。') }}
        {{ t('沒有在 manifest 宣告的能力（連外、機密）一律沒有。') }}
      </CAlert>

      <div class="table-responsive">
        <CTable align="middle" bordered class="mb-0">
          <CTableHead class="text-nowrap">
            <CTableRow>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('名稱') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('說明') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('識別字') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('版本') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('能力') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('安裝時間') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('狀態') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
            </CTableRow>
          </CTableHead>
          <CTableBody>
            <CTableRow v-for="plugin in pluginPage.paged" :key="plugin.pluginId">
              <CTableDataCell class="fw-semibold text-nowrap">{{ plugin.manifest.name }}</CTableDataCell>
              <CTableDataCell class="small text-body-secondary text-truncate" style="max-width: 18rem" :title="plugin.manifest.description">
                {{ plugin.manifest.description || '—' }}
              </CTableDataCell>
              <CTableDataCell class="font-monospace small">{{ plugin.pluginId }}</CTableDataCell>
              <CTableDataCell class="small">{{ plugin.manifest.version }}</CTableDataCell>
              <CTableDataCell class="small">
                <span v-if="plugin.manifest.permissions.http.length" class="badge text-bg-light me-1">
                  {{ t('連外') }} {{ plugin.manifest.permissions.http.length }}
                </span>
                <span v-if="plugin.manifest.permissions.secrets.length" class="badge text-bg-light me-1">
                  {{ t('機密') }} {{ plugin.manifest.permissions.secrets.length }}
                </span>
                <span v-if="plugin.isTrusted" class="badge text-bg-warning">{{ t('信任') }}</span>
                <span
                  v-if="!plugin.manifest.permissions.http.length && !plugin.manifest.permissions.secrets.length"
                  class="text-body-secondary"
                >
                  —
                </span>
              </CTableDataCell>
              <CTableDataCell class="small text-body-secondary">{{ dateTime(plugin.installedAt) }}</CTableDataCell>
              <CTableDataCell>
                <CBadge :color="plugin.isDisabled ? 'secondary' : 'success'">
                  {{ plugin.isDisabled ? t('停用') : t('啟用') }}
                </CBadge>
              </CTableDataCell>
              <CTableDataCell class="text-end text-nowrap">
                <CButton color="secondary" variant="ghost" size="sm" @click="toggle(plugin)">
                  {{ plugin.isDisabled ? t('啟用') : t('停用') }}
                </CButton>
                <CButton color="danger" variant="ghost" size="sm" @click="remove(plugin)">{{ t('移除') }}</CButton>
              </CTableDataCell>
            </CTableRow>
            <CTableRow v-if="plugins.length === 0">
              <CTableDataCell colspan="8" class="text-center text-body-secondary py-5">
                {{ t('還沒有安裝任何外掛。') }}
              </CTableDataCell>
            </CTableRow>
          </CTableBody>
        </CTable>
      </div>
    </CCardBody>
    <TablePagination
      :page="pluginPage.page"
      :page-size="pluginPage.pageSize"
      :total="pluginPage.total"
      :pages="pluginPage.pages"
      @update:page="pluginPage.setPage"
      @update:page-size="pluginPage.setPageSize"
    />
  </CCard>

  <CModal size="xl" :visible="editing !== null" @close="editing = null">
    <CModalHeader>
      <CModalTitle>{{ t('安裝外掛') }}</CModalTitle>
    </CModalHeader>
    <CModalBody v-if="editing">
      <div class="border rounded p-3 mb-3 bg-body-tertiary">
        <CFormLabel for="plugin-requirement" class="fw-semibold">
          {{ t('用 AI 產生草稿') }}
        </CFormLabel>
        <CInputGroup>
          <CFormInput
            id="plugin-requirement"
            v-model="requirement"
            :placeholder="t('例如：把指定目錄壓成 zip 並上傳到內部檔案伺服器')"
            @keyup.enter="generate"
          />
          <CButton color="secondary" :disabled="generating || !requirement.trim()" @click="generate">
            <CSpinner v-if="generating" size="sm" class="me-2" />{{ t('產生') }}
          </CButton>
        </CInputGroup>
        <CFormText>
          {{ t('產生的是草稿：權限一律被移除、類型與 API 版本會被寫死，安裝前請自己讀過並預覽。') }}
          {{ t('需求文字會送到你設定的 AI 供應商，不要寫入密碼或金鑰。') }}
        </CFormText>
      </div>

      <CAlert v-if="warnings.length" color="warning" class="py-2">
        <ul class="mb-0 ps-3 small">
          <li v-for="warning in warnings" :key="warning">{{ warning }}</li>
        </ul>
      </CAlert>

      <CRow class="g-3">
        <CCol :md="5">
          <CFormLabel for="plugin-manifest">manifest</CFormLabel>
          <CFormTextarea id="plugin-manifest" v-model="editing.manifest" class="font-monospace" rows="16" />
        </CCol>
        <CCol :md="7">
          <CFormLabel for="plugin-code">{{ t('程式碼') }}</CFormLabel>
          <CFormTextarea id="plugin-code" v-model="editing.code" class="font-monospace" rows="16" />
        </CCol>
        <CCol :xs="12">
          <CFormSwitch id="plugin-trusted" v-model="editing.isTrusted" :label="t('標記為信任')" />
          <CFormText>{{ t('不論是否信任，外掛產生的 shell 都會在建置中執行；「信任」只放行 manifest 宣告的連外與機密。別人給的外掛請先預覽再決定。') }}</CFormText>
        </CCol>
        <CCol v-if="preview" :xs="12">
          <CAlert :color="preview.ok ? 'success' : 'danger'" class="mb-0">
            <div class="fw-semibold mb-2">{{ preview.ok ? t('產生的腳本') : t('預覽失敗') }}</div>
            <pre class="mb-0 small" style="white-space: pre-wrap">{{ preview.text }}</pre>
          </CAlert>
        </CCol>
      </CRow>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" class="me-auto" @click="runPreview">
        {{ t('預覽產生的腳本') }}
      </CButton>
      <CButton color="secondary" variant="ghost" @click="editing = null">{{ t('取消') }}</CButton>
      <CButton color="primary" :disabled="saving" @click="install">
        <CSpinner v-if="saving" size="sm" class="me-2" />{{ t('安裝') }}
      </CButton>
    </CModalFooter>
  </CModal>
</template>
