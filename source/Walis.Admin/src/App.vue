<script setup lang="ts">
import { onMounted } from 'vue'
import DefaultLayout from './layout/DefaultLayout.vue'
import SignInView from './views/SignInView.vue'
import OtpRequiredView from './views/OtpRequiredView.vue'
import { onOtpSetupRequiredResponse, onUnauthorizedResponse } from './services/api'
import { restartApplication } from './services/browserSecurity'
import { useAppStore } from './stores/app'
import { useAuthStore } from './stores/auth'
import { useJobsStore } from './stores/jobs'
import { useLocalizationStore } from './stores/localization'
import { useAiStore } from './stores/ai'
import { useRunnersStore } from './stores/runners'
import { useSettingsStore } from './stores/settings'

const app = useAppStore()
const auth = useAuthStore()
const settings = useSettingsStore()
const localization = useLocalizationStore()
const jobs = useJobsStore()
const runners = useRunnersStore()
const ai = useAiStore()

async function bootstrap(): Promise<void> {
  app.connectionError = null

  try {
    await auth.refresh()

    if (auth.needsSignIn || auth.needsOtpSetup) {
      // Until the one-time password is set up the backend blocks every other /api call, so do not load yet or the screen fills with errors.
      return
    }

    await Promise.all([settings.load(true), localization.load(true)])
    await Promise.all([jobs.load(true), runners.load(true), ai.load(true)])
  } catch (error) {
    app.connectionError = error instanceof Error ? error.message : String(error)
  } finally {
    app.ready = true
  }
}

onUnauthorizedResponse(() => {
  const hadAuthenticatedUser = auth.user !== null
  auth.clear()
  auth.requireSignIn = true

  if (hadAuthenticatedUser) {
    restartApplication()
  }
})

// The setting may be turned on after sign-in; signed-in users must then be sent to finish the setup.
onOtpSetupRequiredResponse(() => {
  auth.requireOtp = true
})

onMounted(bootstrap)
</script>

<template>
  <SignInView v-if="app.ready && auth.needsSignIn" @signed-in="bootstrap" />
  <OtpRequiredView v-else-if="app.ready && auth.needsOtpSetup" @done="bootstrap" />
  <DefaultLayout v-else :on-retry="bootstrap" />
</template>
