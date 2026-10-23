<script setup lang="ts">
import { onMounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'

/**
 * A page's top-level tabs: one card with a tab bar on top, each tab's content in the slot of the same name
 * (usually a CCardBody, followed by a CCardFooter when needed). Content is switched with v-show,
 * so forms and scroll positions survive switching back. The active tab syncs to ?tab= in the URL, so a reload does not jump back to the first tab.
 */
const props = defineProps<{
  tabs: { key: string; label: string; icon?: string; badge?: string | number | null }[]
  modelValue: string
}>()
const emit = defineEmits<{ (event: 'update:modelValue', value: string): void }>()

const { t } = useI18n()
const route = useRoute()
const router = useRouter()

function select(key: string): void {
  emit('update:modelValue', key)
}

onMounted(() => {
  const wanted = route.query.tab
  if (typeof wanted === 'string' && props.tabs.some((tab) => tab.key === wanted) && wanted !== props.modelValue) {
    emit('update:modelValue', wanted)
  }
})

watch(
  () => props.modelValue,
  (value) => {
    if (route.query.tab === value) return
    const query = { ...route.query }
    if (value === props.tabs[0]?.key) {
      delete query.tab
    } else {
      query.tab = value
    }
    void router.replace({ query })
  }
)
</script>

<template>
  <CCard class="mb-4">
    <slot name="above" />
    <CCardHeader class="pb-0">
      <CNav variant="underline-border" role="tablist" class="card-header-tabs">
        <CNavItem v-for="tab in tabs" :key="tab.key">
          <button
            type="button"
            class="nav-link"
            role="tab"
            :class="{ active: modelValue === tab.key }"
            :aria-selected="modelValue === tab.key"
            @click="select(tab.key)"
          >
            <CIcon v-if="tab.icon" :icon="tab.icon" class="me-1" />{{ t(tab.label) }}
            <span v-if="tab.badge !== undefined && tab.badge !== null && tab.badge !== ''" class="badge rounded-pill text-bg-secondary ms-1">{{ tab.badge }}</span>
          </button>
        </CNavItem>
      </CNav>
    </CCardHeader>
    <template v-for="tab in tabs" :key="tab.key">
      <div v-show="modelValue === tab.key">
        <slot :name="tab.key" />
      </div>
    </template>
  </CCard>
</template>
