<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { BuildParameterDefinition, BuildParameterType } from '../types'

// Goes in and out as a JSON string (the format stored on the job) and is expanded into a table while editing.
const props = defineProps<{ modelValue: string | null }>()
const emit = defineEmits<{ (event: 'update:modelValue', value: string): void }>()

const { t } = useI18n()

const types: { value: BuildParameterType; label: string }[] = [
  { value: 'string', label: '文字' },
  { value: 'text', label: '多行文字' },
  { value: 'choice', label: '選單' },
  { value: 'boolean', label: '布林' },
  { value: 'password', label: '密碼' }
]

const items = computed<BuildParameterDefinition[]>(() => {
  if (!props.modelValue?.trim()) {
    return []
  }

  try {
    const parsed = JSON.parse(props.modelValue)
    return Array.isArray(parsed) ? parsed : []
  } catch {
    return []
  }
})

const invalidJson = computed(() => {
  if (!props.modelValue?.trim()) {
    return false
  }

  try {
    return !Array.isArray(JSON.parse(props.modelValue))
  } catch {
    return true
  }
})

function write(next: BuildParameterDefinition[]): void {
  emit('update:modelValue', next.length === 0 ? '' : JSON.stringify(next, null, 2))
}

function add(): void {
  write([...items.value, { name: '', label: '', type: 'string', default: '', description: '', options: [] }])
}

function patch(index: number, changes: Partial<BuildParameterDefinition>): void {
  const next = items.value.map((item, position) => (position === index ? { ...item, ...changes } : item))
  const target = next[index]

  if (target && target.type !== 'choice') {
    target.options = []
  }

  write(next)
}

function remove(index: number): void {
  write(items.value.filter((_, position) => position !== index))
}

function optionsText(item: BuildParameterDefinition): string {
  return (item.options ?? []).join('\n')
}

function setOptions(index: number, text: string): void {
  patch(index, { options: text.split('\n').map((line) => line.trim()).filter((line) => line.length > 0) })
}
</script>

<template>
  <div>
    <CAlert v-if="invalidJson" color="warning" class="py-2">{{ t('參數設定不是有效的 JSON，請修正後再編輯。') }}</CAlert>

    <div v-for="(item, index) in items" :key="index" class="border rounded p-3 mb-2">
      <CRow class="g-2">
        <CCol :md="4">
          <CFormLabel class="small mb-1">{{ t('名稱') }}</CFormLabel>
          <CFormInput
            :model-value="item.name"
            class="font-monospace"
            placeholder="DEPLOY_ENV"
            @update:model-value="(value: string) => patch(index, { name: value })"
          />
        </CCol>
        <CCol :md="4">
          <CFormLabel class="small mb-1">{{ t('標籤') }}</CFormLabel>
          <CFormInput
            :model-value="item.label ?? ''"
            :placeholder="t('顯示在觸發表單上的名稱')"
            @update:model-value="(value: string) => patch(index, { label: value })"
          />
        </CCol>
        <CCol :md="3">
          <CFormLabel class="small mb-1">{{ t('類型') }}</CFormLabel>
          <CFormSelect
            :model-value="item.type"
            @update:model-value="(value: string) => patch(index, { type: value as BuildParameterType })"
          >
            <option v-for="type in types" :key="type.value" :value="type.value">{{ t(type.label) }}</option>
          </CFormSelect>
        </CCol>
        <CCol :md="1" class="d-flex align-items-end justify-content-end">
          <CButton color="danger" variant="ghost" size="sm" :title="t('移除')" @click="remove(index)">
            <CIcon icon="cil-trash" />
          </CButton>
        </CCol>
        <CCol v-if="item.type === 'choice'" :md="6">
          <CFormLabel class="small mb-1">{{ t('選項（一行一個）') }}</CFormLabel>
          <CFormTextarea
            :model-value="optionsText(item)"
            class="font-monospace"
            rows="3"
            @update:model-value="(value: string) => setOptions(index, value)"
          />
        </CCol>
        <CCol v-if="item.type !== 'password'" :md="item.type === 'choice' ? 6 : 6">
          <CFormLabel class="small mb-1">{{ t('預設值') }}</CFormLabel>
          <CFormSelect
            v-if="item.type === 'boolean'"
            :model-value="item.default === 'true' ? 'true' : 'false'"
            @update:model-value="(value: string) => patch(index, { default: value })"
          >
            <option value="false">false</option>
            <option value="true">true</option>
          </CFormSelect>
          <CFormInput
            v-else
            :model-value="item.default ?? ''"
            class="font-monospace"
            @update:model-value="(value: string) => patch(index, { default: value })"
          />
        </CCol>
        <CCol :xs="12">
          <CFormLabel class="small mb-1">{{ t('說明') }}</CFormLabel>
          <CFormInput
            :model-value="item.description ?? ''"
            @update:model-value="(value: string) => patch(index, { description: value })"
          />
        </CCol>
      </CRow>
    </div>

    <CButton color="secondary" variant="outline" size="sm" @click="add">
      <CIcon icon="cil-plus" class="me-1" />{{ t('新增參數') }}
    </CButton>
  </div>
</template>
