<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()

interface Crumb {
  label: string
  to?: string
}

const crumbs = computed<Crumb[]>(() => {
  const items: Crumb[] = [{ label: 'Walis', to: '/dashboard' }]
  const parent = route.meta.parent as string | undefined

  if (parent) {
    const parentRoute = router.getRoutes().find((item) => item.name === parent)

    if (parentRoute) {
      items.push({ label: t(parentRoute.meta.title as string), to: parentRoute.path })
    }
  }

  items.push({ label: t((route.meta.title as string) ?? '總覽') })
  return items
})
</script>

<template>
  <nav :aria-label="t('麵包屑')">
    <ol class="breadcrumb my-0">
      <li
        v-for="(crumb, index) in crumbs"
        :key="index"
        class="breadcrumb-item"
        :class="{ active: index === crumbs.length - 1 }"
      >
        <RouterLink v-if="crumb.to && index !== crumbs.length - 1" :to="crumb.to">{{ crumb.label }}</RouterLink>
        <span v-else>{{ crumb.label }}</span>
      </li>
    </ol>
  </nav>
</template>
