import { computed, reactive, ref, watch } from 'vue'

export const PAGE_SIZES = [25, 50, 100, 200]
export const DEFAULT_PAGE_SIZE = 25

/**
 * Client-side paging for a list that is already loaded: it slices the rows and keeps the page inside range.
 * Lists that are paged in the database (jobs, builds) keep their own state and only share the footer component.
 */
export function usePagedRows<T>(rows: () => T[], defaultPageSize: number = DEFAULT_PAGE_SIZE) {
  const page = ref(1)
  const pageSize = ref(defaultPageSize)

  const all = computed(() => rows())
  const total = computed(() => all.value.length)
  const pages = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
  const paged = computed(() => all.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))

  // Filtering or deleting rows can leave the current page past the end.
  watch(pages, (value) => {
    if (page.value > value) page.value = value
  })

  function setPage(value: number): void {
    page.value = value
  }

  function setPageSize(value: number): void {
    pageSize.value = value
    page.value = 1
  }

  // reactive() keeps the call sites to one line: the template reads state.page without unwrapping refs itself.
  return reactive({ page, pageSize, total, pages, paged, setPage, setPageSize })
}
