<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { CCardFooter } from '@coreui/vue'
import { PAGE_SIZES } from '../composables/usePagedRows'

/**
 * The footer under a table: rows per page, which rows are being shown, and the page buttons.
 * It only displays and reports the choice, so client-side and database paging can both use it.
 */
const props = withDefaults(
  defineProps<{
    page: number
    pageSize: number
    total: number
    pages: number
    pageSizes?: number[]
    /** Ready-made summary text; without it the generic wording is used. */
    summary?: string
    /** For a table that is not inside a card: the same row without the card footer's frame. */
    plain?: boolean
  }>(),
  { pageSizes: () => PAGE_SIZES, summary: undefined, plain: false }
)

const emit = defineEmits<{
  (event: 'update:page', value: number): void
  (event: 'update:pageSize', value: number): void
}>()

const { t } = useI18n()

const rangeFrom = computed(() => (props.total === 0 ? 0 : (props.page - 1) * props.pageSize + 1))
const rangeTo = computed(() => Math.min(props.total, props.page * props.pageSize))

/** Page number window: at most seven cells, with ellipses at both ends when there are too many pages. */
const pageItems = computed<(number | '…')[]>(() => {
  const count = props.pages
  const current = props.page

  if (count <= 7) {
    return Array.from({ length: count }, (_, index) => index + 1)
  }

  const window = new Set<number>([1, count, current - 1, current, current + 1])
  if (current <= 3) [2, 3, 4].forEach((value) => window.add(value))
  if (current >= count - 2) [count - 3, count - 2, count - 1].forEach((value) => window.add(value))

  const sorted = [...window].filter((value) => value >= 1 && value <= count).sort((a, b) => a - b)
  const output: (number | '…')[] = []

  sorted.forEach((value, index) => {
    if (index > 0 && value - (sorted[index - 1] as number) > 1) {
      output.push('…')
    }
    output.push(value)
  })

  return output
})

function goTo(target: number): void {
  if (target >= 1 && target <= props.pages && target !== props.page) {
    emit('update:page', target)
  }
}
</script>

<template>
  <component :is="plain ? 'div' : CCardFooter" class="d-flex flex-wrap align-items-center gap-3 small" :class="plain ? 'mt-3' : ''">
    <CFormSelect
      class="w-auto"
      size="sm"
      :model-value="pageSize"
      :aria-label="t('每頁 {size} 筆', { size: pageSize })"
      @update:model-value="(value: string | number) => emit('update:pageSize', Number(value))"
    >
      <option v-for="size in pageSizes" :key="size" :value="size">{{ t('每頁 {size} 筆', { size }) }}</option>
    </CFormSelect>

    <span class="text-body-secondary me-auto">
      <template v-if="summary">{{ summary }}</template>
      <template v-else-if="total === 0">{{ t('共 0 筆') }}</template>
      <template v-else>
        {{ t('顯示第 {from}–{to} 筆，共 {total} 筆', { from: rangeFrom, to: rangeTo, total }) }}
      </template>
    </span>

    <nav v-if="pages > 1" :aria-label="t('第 {page} / {pages} 頁', { page, pages })">
      <ul class="pagination pagination-sm mb-0">
        <li class="page-item" :class="{ disabled: page === 1 }">
          <button type="button" class="page-link" :title="t('第一頁')" :disabled="page === 1" @click="goTo(1)">«</button>
        </li>
        <li class="page-item" :class="{ disabled: page === 1 }">
          <button type="button" class="page-link" :title="t('上一頁')" :disabled="page === 1" @click="goTo(page - 1)">
            ‹
          </button>
        </li>
        <li
          v-for="(item, index) in pageItems"
          :key="`${item}-${index}`"
          class="page-item"
          :class="{ active: item === page, disabled: item === '…' }"
        >
          <span v-if="item === '…'" class="page-link">…</span>
          <button
            v-else
            type="button"
            class="page-link"
            :aria-current="item === page ? 'page' : undefined"
            @click="goTo(item)"
          >
            {{ item }}
          </button>
        </li>
        <li class="page-item" :class="{ disabled: page === pages }">
          <button
            type="button"
            class="page-link"
            :title="t('下一頁')"
            :disabled="page === pages"
            @click="goTo(page + 1)"
          >
            ›
          </button>
        </li>
        <li class="page-item" :class="{ disabled: page === pages }">
          <button
            type="button"
            class="page-link"
            :title="t('最後一頁')"
            :disabled="page === pages"
            @click="goTo(pages)"
          >
            »
          </button>
        </li>
      </ul>
    </nav>
  </component>
</template>
