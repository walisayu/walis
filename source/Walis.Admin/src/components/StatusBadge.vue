<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { BuildStatus } from '../types'

const props = defineProps<{ status: BuildStatus | null | undefined; withIcon?: boolean }>()

const { t } = useI18n()

const presets: Record<BuildStatus, { color: string; icon: string; label: string }> = {
  Queued: { color: 'info', icon: 'cil-clock', label: '佇列中' },
  Running: { color: 'warning', icon: 'cil-reload', label: '執行中' },
  Succeeded: { color: 'success', icon: 'cil-check-circle', label: '成功' },
  Failed: { color: 'danger', icon: 'cil-x-circle', label: '失敗' },
  Canceled: { color: 'secondary', icon: 'cil-ban', label: '已取消' }
}

const preset = computed(() => (props.status ? presets[props.status] : null))
</script>

<template>
  <CBadge v-if="preset" :color="preset.color" :text-bg-color="preset.color">
    <CIcon v-if="withIcon !== false" :icon="preset.icon" class="me-1" />
    {{ t(preset.label) }}
  </CBadge>
  <span v-else class="text-body-secondary">—</span>
</template>
