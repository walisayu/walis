<script setup lang="ts">
import { reactive, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { parseParameterDefinitions } from '../utils/buildParameters'
import type { BuildParameterDefinition, Job } from '../types'

// When a job declares parameters, the user fills in values before triggering; jobs without declarations never open this dialog.
const props = defineProps<{ job: Job | null }>()
const emit = defineEmits<{
  (event: 'close'): void
  (event: 'run', job: Job, parameters: Record<string, string>): void
}>()

const { t } = useI18n()

const definitions = reactive<BuildParameterDefinition[]>([])
const values = reactive<Record<string, string>>({})

watch(
  () => props.job,
  (job) => {
    definitions.splice(0, definitions.length, ...parseParameterDefinitions(job))
    for (const key of Object.keys(values)) {
      delete values[key]
    }

    for (const definition of definitions) {
      values[definition.name] =
        definition.type === 'boolean'
          ? definition.default === 'true' ? 'true' : 'false'
          : definition.type === 'choice'
            ? definition.default ?? definition.options?.[0] ?? ''
            : definition.default ?? ''
    }
  },
  { immediate: true }
)

function submit(): void {
  if (props.job) {
    emit('run', props.job, { ...values })
  }
}
</script>

<template>
  <CModal :visible="job !== null" @close="emit('close')">
    <CModalHeader>
      <CModalTitle>{{ t('執行「{name}」', { name: job?.name ?? '' }) }}</CModalTitle>
    </CModalHeader>
    <CModalBody>
      <p class="text-body-secondary small">{{ t('填好建置參數後開始執行；值會以環境變數提供給腳本。') }}</p>
      <div v-for="definition in definitions" :key="definition.name" class="mb-3">
        <CFormLabel :for="`param-${definition.name}`">
          {{ definition.label || definition.name }}
          <code class="ms-1 small text-body-secondary">${{ definition.name }}</code>
        </CFormLabel>
        <CFormSelect v-if="definition.type === 'choice'" :id="`param-${definition.name}`" v-model="values[definition.name]">
          <option v-for="option in definition.options ?? []" :key="option" :value="option">{{ option }}</option>
        </CFormSelect>
        <CFormSwitch
          v-else-if="definition.type === 'boolean'"
          :id="`param-${definition.name}`"
          :model-value="values[definition.name] === 'true'"
          :label="values[definition.name] === 'true' ? 'true' : 'false'"
          @update:model-value="(value: boolean) => (values[definition.name] = value ? 'true' : 'false')"
        />
        <CFormTextarea v-else-if="definition.type === 'text'" :id="`param-${definition.name}`" v-model="values[definition.name]" rows="3" />
        <CFormInput
          v-else
          :id="`param-${definition.name}`"
          v-model="values[definition.name]"
          :type="definition.type === 'password' ? 'password' : 'text'"
          autocomplete="off"
        />
        <CFormText v-if="definition.description">{{ definition.description }}</CFormText>
      </div>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="emit('close')">{{ t('取消') }}</CButton>
      <CButton color="primary" @click="submit"><CIcon icon="cil-media-play" class="me-1" />{{ t('立即執行') }}</CButton>
    </CModalFooter>
  </CModal>
</template>
