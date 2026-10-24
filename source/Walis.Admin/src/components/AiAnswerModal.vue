<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import MarkdownText from './MarkdownText.vue'
import { useToastStore } from '../stores/toasts'
import type { AiAnswer } from '../types'

const props = defineProps<{
  visible: boolean
  title: string
  answer: AiAnswer | null
  loading: boolean
  error: string | null
}>()

const emit = defineEmits<{ (event: 'close'): void; (event: 'retry'): void }>()

const { t } = useI18n()
const toasts = useToastStore()

async function copy(): Promise<void> {
  if (!props.answer) {
    return
  }

  try {
    await navigator.clipboard.writeText(props.answer.text)
    toasts.success(t('已複製 AI 回覆'))
  } catch {
    toasts.error(t('瀏覽器不允許存取剪貼簿。'))
  }
}
</script>

<template>
  <CModal size="lg" scrollable :visible="visible" @close="emit('close')">
    <CModalHeader>
      <div>
        <CModalTitle>{{ title }}</CModalTitle>
        <div v-if="answer" class="small text-body-secondary">
          {{ answer.model }} ·
          {{ t('{ms} 毫秒', { ms: answer.durationMs }) }}
          <template v-if="answer.inputTokens !== null">
            · {{ t('輸入 {input} / 輸出 {output} tokens', { input: answer.inputTokens, output: answer.outputTokens ?? 0 }) }}
          </template>
        </div>
      </div>
    </CModalHeader>
    <CModalBody>
      <div v-if="loading" class="d-flex align-items-center gap-3 py-4">
        <CSpinner color="primary" />
        <span class="text-body-secondary">{{ t('AI 分析中，請稍候…') }}</span>
      </div>
      <CAlert v-else-if="error" color="danger" class="mb-0">{{ error }}</CAlert>
      <MarkdownText v-else-if="answer" :text="answer.text" />
    </CModalBody>
    <CModalFooter>
      <CButton v-if="answer" color="secondary" variant="outline" @click="copy">
        <CIcon icon="cil-copy" class="me-1" />{{ t('複製') }}
      </CButton>
      <CButton color="secondary" variant="outline" :disabled="loading" @click="emit('retry')">
        <CIcon icon="cil-reload" class="me-1" />{{ t('重新分析') }}
      </CButton>
      <CButton color="primary" @click="emit('close')">{{ t('關閉') }}</CButton>
    </CModalFooter>
  </CModal>
</template>
