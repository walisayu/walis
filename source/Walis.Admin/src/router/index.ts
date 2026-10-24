import { createRouter, createWebHashHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'

/** meta.title is used for both the breadcrumb and the sidebar; the value is the Traditional Chinese source text (the i18n key). */
const routes: RouteRecordRaw[] = [
  {
    path: '/',
    redirect: '/dashboard'
  },
  {
    path: '/dashboard',
    name: 'dashboard',
    component: () => import('../views/DashboardView.vue'),
    meta: { title: '總覽' }
  },
  {
    path: '/jobs',
    name: 'jobs',
    component: () => import('../views/JobsView.vue'),
    meta: { title: '建置任務' }
  },
  {
    path: '/jobs/new',
    name: 'job-create',
    component: () => import('../views/JobEditorView.vue'),
    meta: { title: '新增任務', parent: 'jobs' }
  },
  {
    path: '/jobs/:id/edit',
    name: 'job-edit',
    component: () => import('../views/JobEditorView.vue'),
    meta: { title: '編輯任務', parent: 'jobs' }
  },
  {
    path: '/builds',
    name: 'builds',
    component: () => import('../views/BuildsView.vue'),
    meta: { title: '執行紀錄' }
  },
  {
    path: '/builds/:id',
    name: 'build-detail',
    component: () => import('../views/BuildsView.vue'),
    meta: { title: '執行紀錄', parent: 'builds' }
  },
  {
    path: '/schedules',
    name: 'schedules',
    component: () => import('../views/SchedulesView.vue'),
    meta: { title: '排程管理' }
  },
  {
    path: '/plugins',
    name: 'plugins',
    component: () => import('../views/PluginsView.vue'),
    meta: { title: '外掛' }
  },
  {
    path: '/runners',
    name: 'runners',
    component: () => import('../views/RunnersView.vue'),
    meta: { title: '執行器' }
  },
  {
    path: '/environment',
    name: 'environment',
    component: () => import('../views/EnvironmentView.vue'),
    meta: { title: '環境變數' }
  },
  {
    path: '/notifications',
    name: 'notifications',
    component: () => import('../views/NotificationsView.vue'),
    meta: { title: '通知設定' }
  },
  {
    path: '/security',
    name: 'security',
    component: () => import('../views/SecurityCenterView.vue'),
    meta: { title: '安全中心' }
  },
  {
    path: '/settings',
    name: 'settings',
    component: () => import('../views/SettingsView.vue'),
    meta: { title: '系統設定' }
  },
  {
    path: '/accounts',
    name: 'accounts',
    component: () => import('../views/AccountsView.vue'),
    meta: { title: '帳號管理' }
  },
  {
    path: '/ai',
    name: 'ai',
    component: () => import('../views/AiView.vue'),
    meta: { title: 'AI 助理' }
  },
  {
    path: '/localization',
    name: 'localization',
    component: () => import('../views/LocalizationView.vue'),
    meta: { title: '語言與翻譯' }
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('../views/NotFoundView.vue'),
    meta: { title: '找不到頁面' }
  }
]

export const router = createRouter({
  history: createWebHashHistory(),
  routes,
  scrollBehavior: () => ({ top: 0 })
})
