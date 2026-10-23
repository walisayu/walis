<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '../../services/api'
import { useActionsStore } from '../../stores/actions'
import { useToastStore } from '../../stores/toasts'
import type { ActionDefinition } from '../../types'

const props = defineProps<{ visible: boolean }>()
const emit = defineEmits<{ (event: 'close'): void }>()

const { t } = useI18n()
const actions = useActionsStore()
const toasts = useToastStore()

const manifest = ref('')
const errors = ref<string[]>([])
const busy = ref(false)
const pendingDelete = ref<ActionDefinition | null>(null)
const reloading = ref(false)
const packError = ref('')

watch(
  () => props.visible,
  (visible) => {
    if (visible) {
      void loadPacks()
    }
  },
  { immediate: true }
)

async function loadPacks(): Promise<void> {
  packError.value = ''
  try {
    await actions.loadPacks()
  } catch (exception) {
    packError.value = exception instanceof Error ? exception.message : String(exception)
  }
}

async function reloadPacks(): Promise<void> {
  reloading.value = true
  packError.value = ''
  try {
    await actions.reloadPacks()
    toasts.success(t('已重新載入 {count} 個動作套件', { count: actions.packs.packs.length }))
  } catch (exception) {
    packError.value = exception instanceof Error ? exception.message : String(exception)
  } finally {
    reloading.value = false
  }
}

const sample = `{
  "id": "team.deploy",
  "name": "部署到測試機",
  "description": "呼叫團隊共用的部署腳本。",
  "category": "deploy",
  "icon": "cil-cloud-upload",
  "parameters": [
    { "name": "target", "label": "目標主機", "type": "text", "required": true }
  ],
  "template": "./scripts/deploy.sh {{target}}"
}`

async function install(): Promise<void> {
  busy.value = true
  errors.value = []

  try {
    const definition = await actions.install(manifest.value)
    manifest.value = ''
    toasts.success(t('已安裝動作 {name}', { name: definition.name }))
  } catch (exception) {
    if (exception instanceof ApiError) {
      errors.value = exception.errors.manifest ?? [exception.message]
    } else {
      errors.value = [exception instanceof Error ? exception.message : String(exception)]
    }
  } finally {
    busy.value = false
  }
}

async function uninstall(): Promise<void> {
  const definition = pendingDelete.value

  if (!definition) {
    return
  }

  try {
    await actions.uninstall(definition.id)
    toasts.error(t('已移除動作 {name}', { name: definition.name }))
  } catch (exception) {
    toasts.error(exception instanceof Error ? exception.message : String(exception))
  } finally {
    pendingDelete.value = null
  }
}

function useSample(): void {
  manifest.value = sample
}
</script>

<template>
  <CModal size="lg" scrollable :visible="visible" @close="emit('close')">
    <CModalHeader>
      <div>
        <CModalTitle>{{ t('管理動作') }}</CModalTitle>
        <div class="small text-body-secondary">
          {{ t('動作是一份 JSON 定義，只宣告欄位與 shell 樣板，不含程式碼。') }}
        </div>
      </div>
    </CModalHeader>

    <CModalBody>
      <h6 class="fw-semibold">{{ t('已安裝的自訂動作') }}</h6>

      <CListGroup v-if="actions.custom.length" class="mb-4">
        <CListGroupItem
          v-for="definition in actions.custom"
          :key="definition.id"
          class="d-flex align-items-center gap-2"
        >
          <CIcon :icon="definition.icon" class="text-body-secondary" />
          <div class="me-auto">
            <div class="fw-semibold">{{ definition.name }}</div>
            <div class="small text-body-secondary font-monospace">
              {{ definition.id }}<span v-if="definition.version"> · v{{ definition.version }}</span>
              <span v-if="definition.author"> · {{ definition.author }}</span>
            </div>
          </div>
          <CButton color="danger" variant="ghost" size="sm" :title="t('移除')" @click="pendingDelete = definition">
            <CIcon icon="cil-trash" />
          </CButton>
        </CListGroupItem>
      </CListGroup>

      <p v-else class="text-body-secondary">{{ t('目前只有內建動作。') }}</p>

      <div class="d-flex align-items-center mt-4 mb-2">
        <h6 class="fw-semibold mb-0 me-auto">{{ t('動作套件') }}</h6>
        <CButton color="secondary" variant="outline" size="sm" :disabled="reloading" @click="reloadPacks">
          <CSpinner v-if="reloading" size="sm" class="me-1" /><CIcon v-else icon="cil-reload" class="me-1" />{{ t('重新載入') }}
        </CButton>
      </div>
      <p class="small text-body-secondary">
        {{ t('套件是放在目錄裡、由 git 管理的一組動作 JSON；把審核過的套件 repo clone 到下列目錄後按重新載入。') }}
      </p>
      <div class="small font-monospace text-body-secondary mb-2">
        <div v-for="root in actions.packs.roots" :key="root">{{ root }}</div>
      </div>

      <CAlert v-if="packError" color="danger" class="py-2">{{ packError }}</CAlert>

      <CListGroup v-if="actions.packs.packs.length" class="mb-2">
        <CListGroupItem v-for="pack in actions.packs.packs" :key="pack.directory">
          <div class="d-flex align-items-center gap-2">
            <CIcon icon="cil-folder" class="text-body-secondary" />
            <span class="fw-semibold">{{ pack.name }}</span>
            <span class="small text-body-secondary font-monospace">v{{ pack.version }}</span>
            <span class="ms-auto small text-body-secondary">{{ t('{count} 個動作', { count: pack.count }) }}</span>
            <CBadge v-if="pack.errors.length" color="danger">{{ t('{count} 個錯誤', { count: pack.errors.length }) }}</CBadge>
          </div>
          <div v-if="pack.description" class="small text-body-secondary">{{ pack.description }}</div>
          <ul v-if="pack.errors.length" class="small text-danger mb-0 mt-1 ps-3">
            <li v-for="(error, index) in pack.errors" :key="index">{{ error }}</li>
          </ul>
        </CListGroupItem>
      </CListGroup>
      <p v-else class="text-body-secondary">{{ t('目前沒有載入任何動作套件。') }}</p>

      <h6 class="fw-semibold mt-4">{{ t('安裝動作') }}</h6>
      <p class="small text-body-secondary">
        {{ t('貼上動作定義的 JSON，安裝前會檢查識別碼、參數與樣板。格式說明見 docs/actions.md。') }}
      </p>

      <CAlert v-if="errors.length" color="danger" class="py-2">
        <div v-for="(error, index) in errors" :key="index">{{ error }}</div>
      </CAlert>

      <CFormTextarea v-model="manifest" class="font-monospace" rows="12" spellcheck="false" />
      <CButton color="link" size="sm" class="p-0 mt-1" @click="useSample">{{ t('填入範例') }}</CButton>
    </CModalBody>

    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="emit('close')">{{ t('關閉') }}</CButton>
      <CButton color="primary" :disabled="busy || manifest.trim().length === 0" @click="install">
        <CSpinner v-if="busy" size="sm" class="me-2" />{{ t('安裝') }}
      </CButton>
    </CModalFooter>
  </CModal>

  <CModal :visible="pendingDelete !== null" @close="pendingDelete = null">
    <CModalHeader><CModalTitle>{{ t('移除動作') }}</CModalTitle></CModalHeader>
    <CModalBody>
      {{ t('確定要移除動作「{name}」嗎？已經在流程裡使用它的任務會編譯失敗。', { name: pendingDelete?.name ?? '' }) }}
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="pendingDelete = null">{{ t('取消') }}</CButton>
      <CButton color="danger" @click="uninstall">{{ t('移除') }}</CButton>
    </CModalFooter>
  </CModal>
</template>
