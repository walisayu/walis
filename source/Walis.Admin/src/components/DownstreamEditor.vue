<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useJobsStore } from '../stores/jobs'
import type { DownstreamTarget, DownstreamWhen } from '../types'

const props = defineProps<{ modelValue: string | null; ownName: string }>()
const emit = defineEmits<{ (event: 'update:modelValue', value: string): void }>()

const { t } = useI18n()
const jobs = useJobsStore()

const whens: { value: DownstreamWhen; label: string }[] = [
  { value: 'success', label: '成功時' },
  { value: 'failure', label: '失敗時' },
  { value: 'always', label: '總是' }
]

const items = computed<DownstreamTarget[]>(() => {
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

const candidates = computed(() => jobs.items.filter((job) => job.name !== props.ownName).map((job) => job.name))

function write(next: DownstreamTarget[]): void {
  emit('update:modelValue', next.length === 0 ? '' : JSON.stringify(next))
}

function add(): void {
  write([...items.value, { jobName: candidates.value[0] ?? '', when: 'success' }])
}

function patch(index: number, changes: Partial<DownstreamTarget>): void {
  write(items.value.map((item, position) => (position === index ? { ...item, ...changes } : item)))
}

function remove(index: number): void {
  write(items.value.filter((_, position) => position !== index))
}
</script>

<template>
  <div>
    <div v-for="(item, index) in items" :key="index" class="d-flex gap-2 align-items-center mb-2">
      <CFormSelect
        :model-value="item.jobName"
        :aria-label="t('下游任務')"
        @update:model-value="(value: string) => patch(index, { jobName: value })"
      >
        <option v-if="item.jobName && !candidates.includes(item.jobName)" :value="item.jobName">{{ item.jobName }}</option>
        <option v-for="name in candidates" :key="name" :value="name">{{ name }}</option>
      </CFormSelect>
      <CFormSelect
        :model-value="item.when"
        style="max-width: 9rem"
        :aria-label="t('觸發時機')"
        @update:model-value="(value: string) => patch(index, { when: value as DownstreamWhen })"
      >
        <option v-for="when in whens" :key="when.value" :value="when.value">{{ t(when.label) }}</option>
      </CFormSelect>
      <CButton color="danger" variant="ghost" size="sm" :title="t('移除')" @click="remove(index)">
        <CIcon icon="cil-trash" />
      </CButton>
    </div>
    <CButton color="secondary" variant="outline" size="sm" @click="add">
      <CIcon icon="cil-plus" class="me-1" />{{ t('新增下游任務') }}
    </CButton>
  </div>
</template>
