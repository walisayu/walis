<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import { usePagedRows } from '../composables/usePagedRows'
import PageTabs from '../components/PageTabs.vue'
import PageActions from '../components/PageActions.vue'
import { SOURCE_LOCALE, setLocale, sourceStrings } from '../i18n'
import { useLocalizationStore } from '../stores/localization'
import { useToastStore } from '../stores/toasts'

const { t, locale } = useI18n()

    const tab = ref<'locales' | 'strings'>('locales')
    const tabs = [
      { key: 'locales', label: '語言', icon: 'cil-language' },
      { key: 'strings', label: '翻譯管理', icon: 'cil-translate' }
    ]
    const localization = useLocalizationStore()
const toasts = useToastStore()

const target = ref('en')
const group = ref('all')
const search = ref('')
const untranslatedOnly = ref(false)
const busy = ref(false)
const importVisible = ref(false)
const importText = ref('')

const groupLabels: Record<string, string> = {
  nav: '導覽',
  header: '頁首',
  common: '共用',
  status: '狀態',
  dashboard: '總覽',
  jobs: '建置任務',
  builds: '執行紀錄',
  schedules: '排程管理',
  runners: '執行器',
  env: '環境變數',
  notify: '通知設定',
  settings: '系統設定',
  editor: '任務編輯',
  actions: '動作流程',
  ai: 'AI 助理',
  i18n: '語言與翻譯',
  toast: '提示訊息',
  data: '示範資料'
}

function groupLabel(group: string): string {
  return t(groupLabels[group] ?? group)
}

const groups = computed(() => {
  const unique = new Set<string>()

  for (const source of sourceStrings) {
    unique.add(localization.groupOf(source))
  }

  return [...unique]
})

const targets = computed(() => localization.locales.filter((item) => item.code !== SOURCE_LOCALE))

const rows = computed(() => {
  const keyword = search.value.trim().toLowerCase()

  return sourceStrings.filter((source) => {
    const value = localization.valueOf(target.value, source)
    const matchesGroup = group.value === 'all' || localization.groupOf(source) === group.value
    const matchesTodo = !untranslatedOnly.value || value.length === 0
    const matchesSearch =
      keyword.length === 0 || source.toLowerCase().includes(keyword) || value.toLowerCase().includes(keyword)

    return matchesGroup && matchesTodo && matchesSearch
  })
})

const progress = computed(() => localization.progress(target.value))

async function saveEntry(source: string, value: string): Promise<void> {
  if (value === localization.valueOf(target.value, source)) {
    return
  }

  try {
    await localization.saveTranslation(target.value, source, value)
    toasts.success(t('已更新 {locale} 的翻譯', { locale: localeName(target.value) }))
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

function localeName(code: string): string {
  return localization.locales.find((item) => item.code === code)?.nativeName ?? code
}

async function autoTranslate(): Promise<void> {
  busy.value = true

  try {
    const count = await localization.autoTranslate(target.value)
    toasts.success(t('已套用 {count} 筆自動翻譯建議', { count }))
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    busy.value = false
  }
}

async function reset(): Promise<void> {
  busy.value = true

  try {
    const count = await localization.reset(target.value)
    toasts.push(t('已清除 {count} 筆翻譯覆寫', { count }), 'secondary')
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    busy.value = false
  }
}

async function exportJson(): Promise<void> {
  const json = localization.exportJson(target.value)

  try {
    await navigator.clipboard.writeText(json)
    toasts.success(t('已複製 {locale} 翻譯檔到剪貼簿', { locale: localeName(target.value) }))
  } catch {
    toasts.error(t('瀏覽器不允許存取剪貼簿。'))
  }
}

async function importJson(): Promise<void> {
  busy.value = true

  try {
    const count = await localization.importJson(target.value, importText.value)
    importVisible.value = false
    importText.value = ''
    toasts.success(t('已匯入 {count} 筆翻譯', { count }))
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    busy.value = false
  }
}

function use(code: string): void {
  setLocale(code)
  toasts.info(t('介面語言已切換為 {name}', { name: localeName(code) }))
}

onMounted(() => localization.load(true))

const sourcePage = usePagedRows(() => rows.value)
</script>

<template>
    <PageActions>
      <CButton color="secondary" variant="outline" size="sm" :disabled="busy" @click="importVisible = true">
        <CIcon icon="cil-cloud-upload" class="me-1" />{{ t('匯入 JSON') }}
      </CButton>
      <CButton color="secondary" variant="outline" size="sm" @click="exportJson">
        <CIcon icon="cil-cloud-download" class="me-1" />{{ t('匯出 JSON') }}
      </CButton>
      <CButton color="secondary" variant="outline" size="sm" :disabled="busy" @click="reset">
        {{ t('清除覆寫') }}
      </CButton>
      <CButton color="primary" size="sm" :disabled="busy" @click="autoTranslate">
        <CSpinner v-if="busy" size="sm" class="me-1" />
        <CIcon v-else icon="cil-translate" class="me-1" />
        {{ t('自動翻譯未完成項目') }}
      </CButton>
    </PageActions>

      <PageTabs v-model="tab" :tabs="tabs">
        <template #locales>
          <CCardBody>
      <CRow class="g-4 mb-4">
    <CCol v-for="item in localization.locales" :key="item.code" :md="6" :xl="4">
      <CCard class="h-100">
        <CCardBody>
          <div class="d-flex align-items-center mb-3">
            <CAvatar color="secondary" size="md" class="me-3"><CIcon icon="cil-language" size="lg" /></CAvatar>
            <div class="me-auto">
              <div class="fw-semibold">{{ item.nativeName }}</div>
              <div class="small text-body-secondary font-monospace">{{ item.code }}</div>
            </div>
            <CBadge v-if="locale === item.code" text-bg-color="primary">{{ t('目前使用') }}</CBadge>
          </div>

          <div class="d-flex justify-content-between small mb-1">
            <span class="text-body-secondary">{{ t('翻譯進度') }}</span>
            <span class="fw-semibold">
              {{ localization.progress(item.code).done }} / {{ localization.progress(item.code).total }}
            </span>
          </div>
          <CProgress
            class="progress-thin mb-3"
            :color="localization.progress(item.code).percent === 100 ? 'success' : localization.progress(item.code).percent >= 50 ? 'info' : 'warning'"
            :value="localization.progress(item.code).percent"
          />

          <div class="small text-body-secondary mb-3">
            <template v-if="item.code === SOURCE_LOCALE">{{ t('來源語言，不需翻譯。') }}</template>
            <template v-else>
              {{ t('尚有 {count} 個字串未翻譯', { count: localization.progress(item.code).total - localization.progress(item.code).done }) }}
            </template>
          </div>

          <CButton
            color="primary"
            variant="outline"
            size="sm"
            :disabled="locale === item.code"
            @click="use(item.code)"
          >
            {{ t('設為介面語言') }}
          </CButton>
        </CCardBody>
      </CCard>
    </CCol>
  </CRow>
      </CCardBody>
        </template>
        <template #strings>
        <CCardBody>
      <p class="text-body-secondary">{{ t('翻譯輸入後立即套用到畫面，未翻譯的字串會直接顯示原文。') }}</p>

      <CRow class="g-3 mb-3">
        <CCol :md="4">
          <CFormLabel for="translation-locale">{{ t('選擇翻譯語言') }}</CFormLabel>
          <CFormSelect id="translation-locale" v-model="target">
            <option v-for="item in targets" :key="item.code" :value="item.code">{{ item.nativeName }}</option>
          </CFormSelect>
        </CCol>
        <CCol :md="4">
          <CFormLabel for="translation-group">{{ t('群組') }}</CFormLabel>
          <CFormSelect id="translation-group" v-model="group">
            <option value="all">{{ t('所有群組') }}</option>
            <option v-for="item in groups" :key="item" :value="item">{{ groupLabel(item) }}</option>
          </CFormSelect>
        </CCol>
        <CCol :md="4">
          <CFormLabel for="translation-search">{{ t('搜尋字串') }}</CFormLabel>
          <CInputGroup>
            <CInputGroupText><CIcon icon="cil-search" /></CInputGroupText>
            <CFormInput id="translation-search" v-model="search" type="search" :placeholder="t('搜尋字串')" />
          </CInputGroup>
        </CCol>
        <CCol :xs="12">
          <CFormSwitch id="untranslated-only" v-model="untranslatedOnly" :label="t('只顯示未翻譯')" />
        </CCol>
      </CRow>

      <div class="table-responsive">
        <CTable align="middle" bordered class="mb-0">
          <CTableHead class="text-nowrap">
            <CTableRow>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('群組') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('原文（繁體中文）') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('翻譯') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('狀態') }}</CTableHeaderCell>
            </CTableRow>
          </CTableHead>
          <CTableBody>
            <CTableRow v-for="source in sourcePage.paged" :key="source">
              <CTableDataCell>
                <CBadge color="light" class="border text-body">{{ groupLabel(localization.groupOf(source)) }}</CBadge>
              </CTableDataCell>
              <CTableDataCell class="text-break">{{ source }}</CTableDataCell>
              <CTableDataCell>
                <CFormInput
                  size="sm"
                  :model-value="localization.valueOf(target, source)"
                  :aria-label="source"
                  @change="(event: Event) => saveEntry(source, (event.target as HTMLInputElement).value)"
                />
              </CTableDataCell>
              <CTableDataCell class="text-end">
                <CBadge :text-bg-color="localization.valueOf(target, source) ? 'success' : 'warning'">
                  {{ t(localization.valueOf(target, source) ? '已翻譯' : '未翻譯') }}
                </CBadge>
                <CBadge v-if="localization.isOverridden(target, source)" color="light" class="border text-body ms-1">
                  {{ t('已覆寫') }}
                </CBadge>
              </CTableDataCell>
            </CTableRow>

            <CTableRow v-if="rows.length === 0">
              <CTableDataCell colspan="4" class="text-center text-body-secondary py-5">
                {{ t('找不到符合條件的字串') }}
              </CTableDataCell>
            </CTableRow>
          </CTableBody>
        </CTable>
      </div>
    </CCardBody>
    <TablePagination
      :page="sourcePage.page"
      :page-size="sourcePage.pageSize"
      :total="sourcePage.total"
      :pages="sourcePage.pages"
      @update:page="sourcePage.setPage"
      @update:page-size="sourcePage.setPageSize"
    />

    <CCardFooter class="text-body-secondary small">
      {{ t('共 {total} 個字串 · 已翻譯 {done} 個 · 未翻譯 {todo} 個', {
        total: progress.total,
        done: progress.done,
        todo: progress.total - progress.done
      }) }}
    </CCardFooter>
    </template>
      </PageTabs>
    
  <CModal size="lg" :visible="importVisible" @close="importVisible = false">
    <CModalHeader>
      <CModalTitle>{{ t('匯入 JSON') }}</CModalTitle>
    </CModalHeader>
    <CModalBody>
      <p class="text-body-secondary">{{ t('貼上「原文：翻譯」格式的 JSON，匯入後會覆蓋既有翻譯。') }}</p>
      <CFormTextarea v-model="importText" class="font-monospace" rows="12" spellcheck="false" />
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="importVisible = false">{{ t('取消') }}</CButton>
      <CButton color="primary" :disabled="busy" @click="importJson">{{ t('匯入') }}</CButton>
    </CModalFooter>
  </CModal>
</template>
