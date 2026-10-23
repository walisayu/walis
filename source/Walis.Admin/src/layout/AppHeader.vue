<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import AppBreadcrumb from './AppBreadcrumb.vue'
import { SOURCE_LOCALE, setLocale } from '../i18n'
import { useTheme } from '../composables/useTheme'
import type { ThemeMode } from '../composables/useTheme'
import { useAppStore } from '../stores/app'
import { useAuthStore } from '../stores/auth'
import { useJobsStore } from '../stores/jobs'
import { useLocalizationStore } from '../stores/localization'
import { useSettingsStore } from '../stores/settings'
import { useToastStore } from '../stores/toasts'

const { t, locale } = useI18n()
const router = useRouter()
const app = useAppStore()
const auth = useAuthStore()
const jobs = useJobsStore()
const localization = useLocalizationStore()
const settings = useSettingsStore()
const toasts = useToastStore()
const { mode, setTheme } = useTheme()

const searchVisible = ref(false)
const search = ref('')

const themeIcon = computed(() =>
  mode.value === 'light' ? 'cil-sun' : mode.value === 'dark' ? 'cil-moon' : 'cil-contrast'
)

const themes: { value: ThemeMode; label: string; icon: string }[] = [
  { value: 'light', label: '淺色', icon: 'cil-sun' },
  { value: 'dark', label: '深色', icon: 'cil-moon' },
  { value: 'auto', label: '自動', icon: 'cil-contrast' }
]

const availableLocales = computed(() =>
  localization.locales.filter((item) => item.isEnabled || item.code === SOURCE_LOCALE)
)

const matches = computed(() => {
  const keyword = search.value.trim().toLowerCase()

  if (keyword.length === 0) {
    return jobs.items.slice(0, 6)
  }

  return jobs.items
    .filter((job) => `${job.name}${job.project ?? ''}`.toLowerCase().includes(keyword))
    .slice(0, 8)
})

function openSearch(): void {
  void jobs.load()
  search.value = ''
  searchVisible.value = true
}

function go(path: string): void {
  searchVisible.value = false
  void router.push(path)
}

const initials = computed(() => {
  const name = (auth.user?.displayName ?? auth.user?.username ?? 'Walis').trim()
  const words = name.split(/\s+/).filter(Boolean)

  return (words.length > 1 ? words[0][0] + words[1][0] : name.slice(0, 2)).toUpperCase()
})

async function signOutEverywhere(): Promise<void> {
  try {
    await auth.signOutEverywhere()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function signOut(): Promise<void> {
  try {
    await auth.signOut()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

function changeLocale(code: string): void {
  setLocale(code)
  const native = localization.locales.find((item) => item.code === code)?.nativeName ?? code
  toasts.info(t('介面語言已切換為 {name}', { name: native }))
}
</script>

<template>
  <CHeader position="sticky" class="p-0 mb-4">
    <CContainer fluid class="border-bottom px-4">
      <!-- Collapsing is done by the sidebar's own hamburger; this appears only while the sidebar is collapsed, to bring the menu back. -->
      <CHeaderToggler
        v-if="!app.sidebarVisible"
        style="margin-inline-start: -14px"
        :aria-label="t('展開側邊欄')"
        :title="t('展開側邊欄')"
        @click="app.setSidebarVisible(true)"
      >
        <CIcon icon="cil-menu" size="lg" />
      </CHeaderToggler>

      <CSearchButton class="d-none d-md-flex" @click="openSearch">
        {{ t('搜尋任務、建置編號') }}
      </CSearchButton>

      <CHeaderNav class="ms-auto">
        <CNavItem class="d-md-none">
          <button class="btn btn-link nav-link" type="button" :title="t('搜尋任務、建置編號')" @click="openSearch">
            <CIcon icon="cil-search" size="lg" />
          </button>
        </CNavItem>
        <CNavItem>
          <RouterLink class="nav-link" to="/notifications" :title="t('通知')">
            <CIcon icon="cil-bell" size="lg" />
          </RouterLink>
        </CNavItem>
        <CNavItem>
          <RouterLink class="nav-link" to="/builds" :title="t('工作佇列')">
            <CIcon icon="cil-list-rich" size="lg" />
          </RouterLink>
        </CNavItem>
      </CHeaderNav>

      <CHeaderNav>
        <li class="nav-item py-1">
          <div class="vr h-100 mx-2 text-body text-opacity-75"></div>
        </li>
        <CDropdown variant="nav-item" placement="bottom-end">
          <CDropdownToggle :caret="false" :aria-label="t('語言')">
            <CIcon icon="cil-language" size="lg" />
          </CDropdownToggle>
          <CDropdownMenu style="--cui-dropdown-min-width: 12rem">
            <CDropdownItem
              v-for="item in availableLocales"
              :key="item.code"
              class="d-flex align-items-center"
              component="button"
              :active="locale === item.code"
              @click="changeLocale(item.code)"
            >
              <CIcon icon="cil-language" class="me-3" />
              <span class="me-auto">{{ item.nativeName }}</span>
              <CBadge color="light" class="border ms-3">{{ localization.progress(item.code).percent }}%</CBadge>
            </CDropdownItem>
            <CDropdownDivider />
            <CDropdownItem component="button" class="d-flex align-items-center" @click="go('/localization')">
              <CIcon icon="cil-translate" class="me-3" />
              {{ t('翻譯管理') }}
            </CDropdownItem>
          </CDropdownMenu>
        </CDropdown>

        <li class="nav-item py-1">
          <div class="vr h-100 mx-2 text-body text-opacity-75"></div>
        </li>
        <CDropdown variant="nav-item" placement="bottom-end">
          <CDropdownToggle :caret="false" :aria-label="t('佈景主題')">
            <CIcon :icon="themeIcon" size="lg" />
          </CDropdownToggle>
          <CDropdownMenu style="--cui-dropdown-min-width: 8rem">
            <CDropdownItem
              v-for="item in themes"
              :key="item.value"
              class="d-flex align-items-center"
              component="button"
              :active="mode === item.value"
              @click="setTheme(item.value)"
            >
              <CIcon :icon="item.icon" class="me-3" />
              {{ t(item.label) }}
            </CDropdownItem>
          </CDropdownMenu>
        </CDropdown>

        <li class="nav-item py-1">
          <div class="vr h-100 mx-2 text-body text-opacity-75"></div>
        </li>
        <CDropdown variant="nav-item" placement="bottom-end">
          <CDropdownToggle class="py-0 pe-0" :caret="false">
            <CAvatar color="secondary" text-color="white" size="md" class="fw-semibold">{{ initials }}</CAvatar>
          </CDropdownToggle>
          <CDropdownMenu class="pt-0">
            <CDropdownHeader class="bg-body-tertiary text-body-secondary fw-semibold rounded-top mb-2">
              {{ auth.user?.displayName ?? t('帳號') }}
            </CDropdownHeader>
            <CDropdownItem component="button" @click="go('/settings')">
              <CIcon icon="cil-settings" class="me-2" />{{ t('系統設定') }}
            </CDropdownItem>
            <CDropdownItem component="button" @click="go('/environment')">
              <CIcon icon="cil-code" class="me-2" />{{ t('環境變數') }}
            </CDropdownItem>
            <CDropdownItem component="button" @click="go('/localization')">
              <CIcon icon="cil-language" class="me-2" />{{ t('語言與翻譯') }}
            </CDropdownItem>
            <CDropdownDivider />
            <CDropdownItem component="button" :disabled="!auth.user" @click="signOut">
              <CIcon icon="cil-account-logout" class="me-2" />{{ t('登出') }}
            </CDropdownItem>
            <CDropdownItem component="button" :disabled="!auth.user" @click="signOutEverywhere">
              <CIcon icon="cil-account-logout" class="me-2" />{{ t('登出所有裝置') }}
            </CDropdownItem>
          </CDropdownMenu>
        </CDropdown>
      </CHeaderNav>
    </CContainer>

    <CContainer fluid class="px-4 d-flex align-items-center gap-2">
      <AppBreadcrumb />
      <!-- A page's own action buttons (e.g. cancel/save in the job editor) are Teleported here, right-aligned on the breadcrumb row. -->
      <div id="page-actions" class="ms-auto d-flex flex-wrap justify-content-end align-items-center gap-2 py-1"></div>
    </CContainer>
  </CHeader>

  <CModal :visible="searchVisible" :aria-label="t('搜尋任務、建置編號')" @close="searchVisible = false">
    <CModalHeader>
      <CFormInput
        v-model="search"
        class="border-0 p-0 rounded-0 shadow-none"
        :placeholder="t('搜尋任務、建置編號')"
        autofocus
      />
    </CModalHeader>
    <CModalBody>
      <p class="text-body-secondary small mb-2">{{ t('建置任務') }}</p>
      <CListGroup v-if="matches.length">
        <CListGroupItem
          v-for="job in matches"
          :key="job.id"
          component="button"
          class="d-flex align-items-center gap-2"
          @click="go(`/jobs/${job.id}/edit`)"
        >
          <CIcon icon="cil-layers" class="text-body-secondary" />
          <span class="me-auto">{{ job.name }}</span>
          <span class="small text-body-secondary">{{ job.project }}</span>
        </CListGroupItem>
      </CListGroup>
      <p v-else class="text-body-secondary mb-0">{{ t('找不到符合條件的任務') }}</p>
    </CModalBody>
  </CModal>
</template>
