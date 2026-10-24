<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ActionDefinition, ActionParameter, ActionStep, StepReferenceSource, StepRunCondition } from '../../types'
import { dependenciesOf, descendantsOf } from '../../utils/stepGraph'

const props = defineProps<{
  step: ActionStep
  definition: ActionDefinition
  /** The keyed steps before this one, offered for "insert a reference". */
  references?: StepReferenceSource[]
  /** The whole flow, for choosing dependencies. */
  steps?: ActionStep[]
}>()
const emit = defineEmits<{ (event: 'update', changes: Partial<ActionStep>): void }>()

const { t } = useI18n()

const keyPattern = /^[a-z][a-z0-9_]{0,39}$/
const keyInvalid = computed(() => !!props.step.key && !keyPattern.test(props.step.key))
const sources = computed(() => props.references ?? [])
const keyHint = computed(() => t('有識別字的步驟，輸出可以被後面的步驟用 ${{ steps.識別字.名稱 }} 引用。'))

// Dependencies: the list shows the other steps; those that would create a cycle (this step's own downstream) cannot be checked.
const allSteps = computed(() => props.steps ?? [])
const currentDependencies = computed(() => dependenciesOf(allSteps.value).get(props.step.id) ?? [])
const blocked = computed(() => descendantsOf(allSteps.value, props.step.id))

function stepTitle(step: ActionStep): string {
  return step.name?.trim() || step.actionId
}

function toggleDependency(id: string, checked: boolean): void {
  const next = checked
    ? [...currentDependencies.value.filter((item) => item !== id), id]
    : currentDependencies.value.filter((item) => item !== id)
  emit('update', { dependsOn: next })
}

function setKey(value: string): void {
  emit('update', { key: value.trim() || null })
}

/** Appends the reference to the current value; the cursor position is hard to get, and appending at the end is the least error-prone. */
function insertReference(parameter: ActionParameter, key: string, name: string): void {
  const expression = `\${{ steps.${key}.${name} }}`
  const current = props.step.parameters[parameter.name] ?? parameter.default ?? ''
  setParameter(parameter, current ? `${current}${expression}` : expression)
}

function setParameter(parameter: ActionParameter, value: string): void {
  emit('update', { parameters: { ...props.step.parameters, [parameter.name]: value } })
}

function valueOf(parameter: ActionParameter): string {
  return props.step.parameters[parameter.name] ?? parameter.default ?? ''
}

function isChecked(parameter: ActionParameter): boolean {
  const value = valueOf(parameter)
  return value !== '' && value !== 'false' && value !== '0'
}
</script>

<template>
  <div class="mb-3">
    <div class="fw-semibold">{{ definition.name }}</div>
    <div class="small text-body-secondary">{{ definition.description }}</div>
    <div class="small text-body-secondary font-monospace">{{ definition.id }}</div>
  </div>

  <CRow class="g-2 mb-3">
    <CCol :xs="7">
      <CFormLabel :for="`step-name-${step.id}`">{{ t('步驟名稱') }}</CFormLabel>
      <CFormInput
        :id="`step-name-${step.id}`"
        :model-value="step.name ?? ''"
        :placeholder="definition.name"
        @update:model-value="(value: string) => emit('update', { name: value || null })"
      />
    </CCol>
    <CCol :xs="5">
      <CFormLabel :for="`step-key-${step.id}`">{{ t('識別字') }}</CFormLabel>
      <CFormInput
        :id="`step-key-${step.id}`"
        class="font-monospace"
        :model-value="step.key ?? ''"
        :invalid="keyInvalid"
        placeholder="build"
        spellcheck="false"
        @update:model-value="setKey"
      />
    </CCol>
  </CRow>
  <CFormText class="d-block mb-3 mt-n2">
    <span v-text="keyHint" />
    <template v-if="definition.outputs?.length">
      {{ t('這個動作會輸出：') }}<code>{{ definition.outputs.map((output) => output.name).join(', ') }}</code>
    </template>
  </CFormText>

  <div v-for="parameter in definition.parameters" :key="parameter.name" class="mb-3">
    <template v-if="parameter.type === 'boolean'">
      <CFormSwitch
        :id="`param-${step.id}-${parameter.name}`"
        :model-value="isChecked(parameter)"
        :label="parameter.label"
        @update:model-value="(value: boolean) => setParameter(parameter, value ? 'true' : 'false')"
      />
    </template>

    <template v-else>
      <CFormLabel :for="`param-${step.id}-${parameter.name}`">
        {{ parameter.label }}
        <span v-if="parameter.required" class="text-danger">*</span>
      </CFormLabel>

      <CFormSelect
        v-if="parameter.type === 'select'"
        :id="`param-${step.id}-${parameter.name}`"
        :model-value="valueOf(parameter)"
        @update:model-value="(value: string) => setParameter(parameter, value)"
      >
        <option v-for="option in parameter.options" :key="option.value" :value="option.value">
          {{ option.label }}
        </option>
      </CFormSelect>

      <CFormTextarea
        v-else-if="parameter.type === 'multiline'"
        :id="`param-${step.id}-${parameter.name}`"
        class="font-monospace"
        rows="5"
        spellcheck="false"
        :model-value="valueOf(parameter)"
        :placeholder="parameter.placeholder ?? ''"
        @update:model-value="(value: string) => setParameter(parameter, value)"
      />

      <CFormInput
        v-else
        :id="`param-${step.id}-${parameter.name}`"
        type="text"
        :class="parameter.type === 'path' ? 'font-monospace' : ''"
        :model-value="valueOf(parameter)"
        :placeholder="parameter.placeholder ?? ''"
        @update:model-value="(value: string) => setParameter(parameter, value)"
      />

      <CDropdown v-if="parameter.type !== 'select' && sources.length" variant="btn-group" class="mt-1">
        <CDropdownToggle color="link" size="sm" class="p-0 text-decoration-none">{{ t('插入前面步驟的輸出') }}</CDropdownToggle>
        <CDropdownMenu>
          <template v-for="source in sources" :key="source.key">
            <CDropdownHeader>{{ source.title }} <code>{{ source.key }}</code></CDropdownHeader>
            <CDropdownItem
              v-for="output in source.outputs.length ? source.outputs : [{ name: 'name', description: '' }]"
              :key="output.name"
              class="font-monospace small"
              :title="output.description"
              @click="insertReference(parameter, source.key, output.name)"
            >
              steps.{{ source.key }}.{{ output.name }}
            </CDropdownItem>
          </template>
        </CDropdownMenu>
      </CDropdown>
    </template>

    <CFormText v-if="parameter.help">{{ parameter.help }}</CFormText>
  </div>

  <hr />

  <CFormSwitch
    :id="`step-enabled-${step.id}`"
    :model-value="step.enabled"
    :label="t('啟用這個步驟')"
    @update:model-value="(value: boolean) => emit('update', { enabled: value })"
  />
  <CFormSwitch
    :id="`step-continue-${step.id}`"
    class="mt-2"
    :model-value="step.continueOnError"
    :label="t('失敗時繼續往下執行')"
    @update:model-value="(value: boolean) => emit('update', { continueOnError: value })"
  />
  <div v-if="allSteps.length > 1" class="mt-3">
    <CFormLabel class="small mb-1">{{ t('依賴步驟') }}</CFormLabel>
    <div class="dependency-list border rounded p-2">
      <CFormCheck
        v-for="other in allSteps.filter((item) => item.id !== step.id)"
        :id="`dep-${step.id}-${other.id}`"
        :key="other.id"
        :model-value="currentDependencies.includes(other.id)"
        :disabled="blocked.has(other.id)"
        :label="stepTitle(other)"
        :title="blocked.has(other.id) ? t('這一步在它的下游，勾了會循環') : ''"
        @update:model-value="(value: boolean) => toggleDependency(other.id, value)"
      />
    </div>
    <CFormText>{{ t('這一步會等勾選的步驟都結束才開始；一個都不勾就是起點，會跟其他起點同時執行。步驟共用工作目錄，環境變數各自獨立。') }}</CFormText>
  </div>

  <CRow class="g-2 mt-2">
    <CCol :xs="7">
      <CFormLabel :for="`step-when-${step.id}`" class="small mb-1">{{ t('執行條件') }}</CFormLabel>
      <CFormSelect
        :id="`step-when-${step.id}`"
        size="sm"
        :model-value="step.runWhen ?? 'success'"
        @update:model-value="(value: string) => emit('update', { runWhen: value as StepRunCondition })"
      >
        <option value="success">{{ t('前面都成功才執行') }}</option>
        <option value="always">{{ t('總是執行') }}</option>
        <option value="failure">{{ t('只在前面失敗時執行') }}</option>
      </CFormSelect>
    </CCol>
    <CCol :xs="5">
      <CFormLabel :for="`step-retries-${step.id}`" class="small mb-1">{{ t('重試次數') }}</CFormLabel>
      <CFormInput
        :id="`step-retries-${step.id}`"
        size="sm"
        type="number"
        min="0"
        max="10"
        :model-value="String(step.retries ?? 0)"
        @update:model-value="(value: string) => emit('update', { retries: Math.min(10, Math.max(0, Number(value) || 0)) })"
      />
    </CCol>
  </CRow>
  <CFormText>{{ t('「總是」與「失敗時」的步驟在前面失敗後仍會執行，用來清理或通知；建置結果仍算失敗。') }}</CFormText>

  <div class="mt-3">
    <CFormLabel :for="`step-condition-${step.id}`" class="small mb-1">{{ t('執行條件（運算式）') }}</CFormLabel>
    <CFormInput
      :id="`step-condition-${step.id}`"
      size="sm"
      class="font-monospace"
      spellcheck="false"
      :model-value="step.condition ?? ''"
      placeholder="steps.check.changed == 'true' && env.BRANCH != 'main'"
      @update:model-value="(value: string) => emit('update', { condition: value.trim() || null })"
    />
    <CFormText>
      {{ t('執行時用前面步驟的輸出與環境變數求值，不成立就略過這一步（不算失敗）。可用 ==、!=、&&、||、!、括號，以及 contains、startsWith、endsWith、matches、empty。') }}
    </CFormText>
  </div>
</template>

<style scoped>
.dependency-list {
  max-height: 12rem;
  overflow-y: auto;
}
</style>
