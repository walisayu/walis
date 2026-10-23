<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import { usePagedRows } from '../composables/usePagedRows'
import PageTabs from '../components/PageTabs.vue'
import PageActions from '../components/PageActions.vue'
import { ApiError } from '../services/api'
import { useFormat } from '../composables/useFormat'
import { useAuthStore } from '../stores/auth'
import { useEnvironmentStore } from '../stores/environment'
import { useJobsStore } from '../stores/jobs'
import { useToastStore } from '../stores/toasts'
import type { EnvironmentVariable, EnvironmentVariableInput } from '../types'

const { t } = useI18n()

const tab = ref<'variables' | 'usage'>('variables')
const tabs = [
  { key: 'variables', label: '環境變數', icon: 'cil-code' },
  { key: 'usage', label: '使用方式', icon: 'cil-info' }
]
const auth = useAuthStore()
const environment = useEnvironmentStore()
const jobs = useJobsStore()
const toasts = useToastStore()
const { relative } = useFormat()

const editorVisible = ref(false)
const editing = ref<EnvironmentVariable | null>(null)
const pendingDelete = ref<EnvironmentVariable | null>(null)
const errors = ref<Record<string, string[]>>({})

// The scope select stores '' for global. CoreUI's select only reports an option's DOM string value, so binding an
// option to null still emits '', which the API cannot bind to a nullable Guid; toInput turns it back into null.
const form = reactive({
  name: '',
  value: '',
  isSecret: true,
  jobId: ''
})

function toInput(): EnvironmentVariableInput {
  return {
    name: form.name,
    value: form.value,
    isSecret: form.isSecret,
    jobId: form.jobId === '' ? null : form.jobId
  }
}

// A secret's field holds the mask, so select it on focus: typing then replaces the value instead of appending to the
// mask, which would silently store a broken secret.
function selectUnchangedSecret(event: FocusEvent): void {
  if (editing.value?.isSecret && form.value === editing.value.value) {
    ;(event.target as HTMLInputElement).select()
  }
}

function openEditor(variable: EnvironmentVariable | null): void {
  editing.value = variable
  errors.value = {}

  Object.assign(form, {
    name: variable?.name ?? '',
    // Keep the value exactly as the list returned it: a masked secret sent back unchanged tells the API to keep the
    // stored value, so the scope or the name can be edited without retyping the secret.
    value: variable?.value ?? '',
    isSecret: variable?.isSecret ?? true,
    jobId: variable?.jobId ?? ''
  })

  editorVisible.value = true
}

function closeEditor(): void {
  editorVisible.value = false
  editing.value = null
  form.name = ''
  form.value = ''
  form.isSecret = true
  form.jobId = ''
  errors.value = {}
}

async function save(): Promise<void> {
  errors.value = {}
  const name = form.name

  try {
    if (editing.value) {
      await environment.update(editing.value.id, toInput())
      toasts.success(t('已更新變數 {name}', { name }))
    } else {
      await environment.create(toInput())
      toasts.success(t('已新增變數 {name}', { name }))
    }

    closeEditor()
  } catch (error) {
    if (error instanceof ApiError) {
      errors.value = error.errors
      toasts.error(error.message)
    } else {
      toasts.error(error instanceof Error ? error.message : String(error))
    }
  }
}

async function remove(): Promise<void> {
  const variable = pendingDelete.value

  if (!variable) {
    return
  }

  try {
    await environment.remove(variable.id)
    toasts.error(t('已刪除變數 {name}', { name: variable.name }))
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    pendingDelete.value = null
  }
}

async function copy(name: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(name)
    toasts.info(t('已複製 {name}', { name }))
  } catch {
    toasts.error(t('瀏覽器不允許存取剪貼簿。'))
  }
}

onMounted(() => Promise.all([environment.load(), jobs.load()]))
onBeforeUnmount(() => {
  closeEditor()
  environment.clearSensitive()
})

const variablePage = usePagedRows(() => environment.items)
</script>

<template>
    <PageActions>
      <CFormSwitch
        v-if="auth.isAdmin"
        id="reveal-secrets"
        class="mb-0 me-2"
        :model-value="environment.reveal"
        :label="t('顯示機密值')"
        @update:model-value="(value: boolean) => environment.setReveal(value)"
      />
      <CButton color="primary" size="sm" @click="openEditor(null)">
        <CIcon icon="cil-plus" class="me-1" />{{ t('新增變數') }}
      </CButton>
    </PageActions>

  <PageTabs v-model="tab" :tabs="tabs">
    <template #variables>
    <CCardBody class="p-0">
      <div class="table-responsive">
        <CTable align="middle" class="mb-0">
          <CTableHead class="text-nowrap">
            <CTableRow>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('名稱') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('值') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('套用範圍') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('更新時間') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
            </CTableRow>
          </CTableHead>
          <CTableBody>
            <CTableRow v-for="variable in variablePage.paged" :key="variable.id">
              <CTableDataCell class="font-monospace fw-semibold">{{ variable.name }}</CTableDataCell>
              <CTableDataCell class="font-monospace text-body-secondary text-break">
                {{ variable.value }}
                <CBadge v-if="variable.isSecret" text-bg-color="warning" class="ms-1">{{ t('機密') }}</CBadge>
              </CTableDataCell>
              <CTableDataCell>
                <CBadge color="light" class="border text-body">{{ variable.jobName ?? t('全域') }}</CBadge>
              </CTableDataCell>
              <CTableDataCell class="text-body-secondary">{{ relative(variable.updatedAt) }}</CTableDataCell>
              <CTableDataCell class="text-end text-nowrap">
                <CButton color="secondary" variant="ghost" size="sm" :title="t('複製名稱')" @click="copy(variable.name)">
                  <CIcon icon="cil-copy" />
                </CButton>
                <CButton color="primary" variant="ghost" size="sm" :title="t('編輯')" @click="openEditor(variable)">
                  <CIcon icon="cil-pencil" />
                </CButton>
                <CButton color="danger" variant="ghost" size="sm" :title="t('刪除')" @click="pendingDelete = variable">
                  <CIcon icon="cil-trash" />
                </CButton>
              </CTableDataCell>
            </CTableRow>

            <CTableRow v-if="environment.items.length === 0">
              <CTableDataCell colspan="5" class="text-center text-body-secondary py-5">
                {{ t('尚未建立環境變數') }}
              </CTableDataCell>
            </CTableRow>
          </CTableBody>
        </CTable>
      </div>
    </CCardBody>
    <TablePagination
      :page="variablePage.page"
      :page-size="variablePage.pageSize"
      :total="variablePage.total"
      :pages="variablePage.pages"
      @update:page="variablePage.setPage"
      @update:page-size="variablePage.setPageSize"
    />
    </template>
    <template #usage>
    <CCardBody>
      <p class="text-body-secondary">
        {{ t('變數會在建置開始前寫入執行環境，於命令中以一般環境變數方式取用。') }}
      </p>
      <pre class="bg-body-tertiary border rounded p-3 mb-0"><code>npm ci
NODE_ENV=$NODE_ENV npm run build
curl -H "Authorization: Bearer $DEPLOY_TOKEN" https://example.com/deploy</code></pre>
    </CCardBody>
    </template>
  </PageTabs>

  <CModal :visible="editorVisible" @close="closeEditor">
    <CModalHeader>
      <CModalTitle>{{ t(editing ? '編輯環境變數' : '新增環境變數') }}</CModalTitle>
    </CModalHeader>
    <CModalBody>
      <CRow class="g-3">
        <CCol :xs="12">
          <CFormLabel for="env-name">{{ t('名稱') }}</CFormLabel>
          <CFormInput id="env-name" v-model="form.name" class="font-monospace" placeholder="NODE_ENV" :invalid="Boolean(errors.name)" />
          <CFormFeedback invalid>{{ errors.name?.[0] }}</CFormFeedback>
        </CCol>
        <CCol :xs="12">
          <CFormLabel for="env-value">{{ t('值') }}</CFormLabel>
          <CFormInput
            id="env-value"
            v-model="form.value"
            :type="form.isSecret ? 'password' : 'text'"
            :autocomplete="form.isSecret ? 'new-password' : 'off'"
            class="font-monospace"
            placeholder="production"
            :invalid="Boolean(errors.value)"
            @focus="selectUnchangedSecret"
          />
          <CFormFeedback invalid>{{ errors.value?.[0] }}</CFormFeedback>
          <CFormText v-if="editing?.isSecret">{{ t('保留遮蔽值代表不變更內容；取消機密時必須重新輸入。') }}</CFormText>
        </CCol>
        <CCol :xs="12">
          <CFormLabel for="env-scope">{{ t('套用範圍') }}</CFormLabel>
          <CFormSelect id="env-scope" v-model="form.jobId">
            <option value="">{{ t('全域') }}</option>
            <option v-for="job in jobs.items" :key="job.id" :value="job.id">{{ job.name }}</option>
          </CFormSelect>
        </CCol>
        <CCol :xs="12">
          <CFormSwitch
            id="env-secret"
            v-model="form.isSecret"
            :label="t('視為機密值（畫面與建置輸出皆遮蔽）')"
          />
        </CCol>
      </CRow>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="closeEditor">{{ t('取消') }}</CButton>
      <CButton color="primary" @click="save">{{ t('儲存') }}</CButton>
    </CModalFooter>
  </CModal>

  <CModal :visible="pendingDelete !== null" @close="pendingDelete = null">
    <CModalHeader>
      <CModalTitle>{{ t('刪除變數') }}</CModalTitle>
    </CModalHeader>
    <CModalBody>{{ t('確定要刪除變數「{name}」嗎？', { name: pendingDelete?.name ?? '' }) }}</CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="pendingDelete = null">{{ t('取消') }}</CButton>
      <CButton color="danger" @click="remove">{{ t('刪除') }}</CButton>
    </CModalFooter>
  </CModal>
</template>
