<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAppStore } from '../stores/app'
import { useJobsStore } from '../stores/jobs'
import { useRunnersStore } from '../stores/runners'

const { t } = useI18n()
const app = useAppStore()
const jobs = useJobsStore()
const runners = useRunnersStore()

const enabledJobs = computed(() => jobs.enabled.length)
const onlineRunners = computed(() => runners.items.filter((runner) => runner.status !== 'Offline').length)
const localRunner = computed(() => runners.items.find((runner) => runner.isLocal))

interface NavEntry {
  title: string
  to: string
  icon: string
  badge?: number
  badgeColor?: string
}

const workspace = computed<NavEntry[]>(() => [
  { title: '總覽', to: '/dashboard', icon: 'cil-speedometer' },
  { title: '建置任務', to: '/jobs', icon: 'cil-layers', badge: enabledJobs.value, badgeColor: 'primary' },
  { title: '執行紀錄', to: '/builds', icon: 'cil-list-rich' },
  { title: '排程管理', to: '/schedules', icon: 'cil-clock' }
])

const resources = computed<NavEntry[]>(() => [
  { title: '執行器', to: '/runners', icon: 'cil-terminal', badge: onlineRunners.value, badgeColor: 'success' },
  { title: '環境變數', to: '/environment', icon: 'cil-code' },
  { title: '通知設定', to: '/notifications', icon: 'cil-bell' },
  { title: '外掛', to: '/plugins', icon: 'cil-puzzle' }
])

const system = computed<NavEntry[]>(() => [
  { title: 'AI 助理', to: '/ai', icon: 'cil-lightbulb' },
  { title: '帳號管理', to: '/accounts', icon: 'cil-user' },
  { title: '安全中心', to: '/security', icon: 'cil-shield-alt' },
  { title: '系統設定', to: '/settings', icon: 'cil-settings' },
  { title: '語言與翻譯', to: '/localization', icon: 'cil-language' }
])
</script>

<template>
  <CSidebar
    class="border-end"
    color-scheme="dark"
    position="fixed"
    :unfoldable="app.sidebarUnfoldable"
    :visible="app.sidebarVisible"
    @visible-change="app.setSidebarVisible"
  >
    <CSidebarHeader class="border-bottom">
      <CSidebarBrand class="me-auto">
        <span class="sidebar-brand-full d-flex align-items-center">
          <span
            class="d-inline-flex align-items-center justify-content-center bg-primary text-white rounded fw-bold me-2"
            style="width: 2rem; height: 2rem"
          >W</span>
          <span>
            <span class="d-block fs-5 fw-semibold lh-sm">Walis</span>
            <span class="d-block small text-body-secondary lh-sm">{{ t('CI/CD 管理中心') }}</span>
          </span>
        </span>
        <span class="sidebar-brand-narrow">
          <span
            class="d-inline-flex align-items-center justify-content-center bg-primary text-white rounded fw-bold"
            style="width: 2rem; height: 2rem"
          >W</span>
        </span>
      </CSidebarBrand>
      <button
        class="sidebar-hamburger d-sidebar-narrow-none"
        type="button"
        :aria-label="t('收合側邊欄')"
        :title="t('收合側邊欄')"
        @click="app.setSidebarVisible(false)"
      >
        <CIcon icon="cil-menu" size="lg" />
      </button>
    </CSidebarHeader>

    <CSidebarNav class="compact">
      <CNavTitle>{{ t('工作區') }}</CNavTitle>
      <CNavItem v-for="entry in workspace" :key="entry.to">
        <RouterLink class="nav-link" active-class="active" :to="entry.to">
          <CIcon custom-class-name="nav-icon" :icon="entry.icon" />
          {{ t(entry.title) }}
          <CBadge v-if="entry.badge" class="ms-auto" size="sm" :color="entry.badgeColor">{{ entry.badge }}</CBadge>
        </RouterLink>
      </CNavItem>

      <CNavTitle>{{ t('資源') }}</CNavTitle>
      <CNavItem v-for="entry in resources" :key="entry.to">
        <RouterLink class="nav-link" active-class="active" :to="entry.to">
          <CIcon custom-class-name="nav-icon" :icon="entry.icon" />
          {{ t(entry.title) }}
          <CBadge v-if="entry.badge" class="ms-auto" size="sm" :color="entry.badgeColor">{{ entry.badge }}</CBadge>
        </RouterLink>
      </CNavItem>

      <CNavTitle>{{ t('系統') }}</CNavTitle>
      <CNavItem v-for="entry in system" :key="entry.to">
        <RouterLink class="nav-link" active-class="active" :to="entry.to">
          <CIcon custom-class-name="nav-icon" :icon="entry.icon" />
          {{ t(entry.title) }}
        </RouterLink>
      </CNavItem>
    </CSidebarNav>

    <CSidebarFooter class="border-top d-none d-md-flex">
      <div v-if="localRunner" class="d-flex align-items-center d-sidebar-narrow-none">
        <span
          class="d-inline-block rounded-circle me-2 flex-shrink-0"
          :class="localRunner.status === 'Offline' ? 'bg-secondary' : 'bg-success'"
          style="width: .5rem; height: .5rem"
        ></span>
        <div class="small lh-sm">
          <div class="fw-semibold">{{ t('本機執行器') }}</div>
          <div class="text-body-secondary">
            {{ t('{used} / {total} 個插槽使用中', { used: localRunner.usedSlots, total: localRunner.totalSlots }) }}
          </div>
        </div>
      </div>
      <CSidebarToggler @click="app.toggleUnfoldable()" />
    </CSidebarFooter>
  </CSidebar>
</template>
