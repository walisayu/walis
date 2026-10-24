import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api } from '../services/api'
import { restartApplication } from '../services/browserSecurity'
import { useEnvironmentStore } from './environment'
import type { SignedInUser, WalisUser } from '../types'

export const useAuthStore = defineStore('auth', () => {
  const environment = useEnvironmentStore()
  const requireSignIn = ref(false)
  const requireOtp = ref(false)
  const hasUsers = ref(false)
  const setupCodeRequired = ref(false)
  const unisolatedBuildsAllowed = ref(false)
  const user = ref<SignedInUser | null>(null)
  const users = ref<WalisUser[]>([])

  const needsSignIn = computed(() => requireSignIn.value && user.value === null)

  /** The system requires every account to enable one-time passwords but this account has not: the backend blocks all of /api, so go set it up first. */
  const needsOtpSetup = computed(() => requireOtp.value && user.value !== null && !user.value.hasOtp)
  const isAdmin = computed(() => user.value?.role === 'admin')
  const canWrite = computed(() => user.value?.role === 'admin' || user.value?.role === 'operator')

  async function refresh(): Promise<void> {
    const previousIdentity = user.value ? `${user.value.username}:${user.value.role}` : null
    const status = await api.authStatus()
    const nextIdentity = status.user ? `${status.user.username}:${status.user.role}` : null

    if (previousIdentity !== nextIdentity || status.user?.role !== 'admin') {
      environment.clearSensitive()
    }

    requireSignIn.value = status.requireSignIn
    requireOtp.value = status.requireOtp
    hasUsers.value = status.hasUsers
    setupCodeRequired.value = status.setupCodeRequired ?? false
    unisolatedBuildsAllowed.value = status.unisolatedBuildsAllowed ?? false
    user.value = status.user

    // A logout, account switch, disable, or role change invalidates every
    // authorization-sensitive store, not just the environment-variable store.
    if (previousIdentity !== null && previousIdentity !== nextIdentity) {
      restartApplication()
    }
  }

  /** A returned challenge code means a one-time password is still required. */
  async function signIn(username: string, password: string): Promise<string | null> {
    environment.clearSensitive()
    const result = await api.signIn(username, password)

    if (result.otpRequired) {
      return result.challenge ?? null
    }

    user.value = result.user!
    hasUsers.value = true
    return null
  }

  async function verifyOtp(challenge: string, code: string): Promise<void> {
    environment.clearSensitive()
    const result = await api.verifyOtp(challenge, code)
    user.value = result.user!
    hasUsers.value = true
  }

  async function setupAccount(
    username: string,
    displayName: string,
    password: string,
    bootstrapToken = '',
    allowUnisolatedBuilds: boolean | null = null
  ): Promise<void> {
    environment.clearSensitive()
    const result = await api.setupAccount(username, displayName, password, bootstrapToken, allowUnisolatedBuilds)
    user.value = result.user!
    hasUsers.value = true
  }

  /** A lost device or a session left open elsewhere: every session of this account ends, then the app restarts. */
  async function signOutEverywhere(): Promise<void> {
    await api.signOutEverywhere()
    user.value = null
    users.value = []
    environment.clearSensitive()
    restartApplication()
  }

  async function signOut(): Promise<void> {
    // Do not present a false logout when the server could not invalidate the
    // HttpOnly session cookie. Reload the document only after success.
    await api.signOut()
    user.value = null
    users.value = []
    environment.clearSensitive()
    restartApplication()
  }

  async function loadUsers(): Promise<void> {
    users.value = await api.accounts()
    hasUsers.value = users.value.length > 0
  }

  async function createUser(username: string, displayName: string, password: string, role: string): Promise<void> {
    await api.createAccount(username, displayName, password, role)
    await loadUsers()
  }

  async function updateUser(id: string, displayName: string, role: string, isDisabled: boolean): Promise<void> {
    const changesCurrentUser = users.value.some(
      (candidate) => candidate.id === id && candidate.username === user.value?.username
    )

    await api.updateAccount(id, displayName, role, isDisabled)

    if (changesCurrentUser) {
      await refresh()
    }

    await loadUsers()
  }

  async function resetPassword(id: string, newPassword: string): Promise<void> {
    const changesCurrentUser = users.value.some(
      (candidate) => candidate.id === id && candidate.username === user.value?.username
    )

    await api.resetAccountPassword(id, newPassword)

    if (changesCurrentUser) {
      await refresh()
    }
  }

  async function resetOtp(id: string): Promise<void> {
    const changesCurrentUser = users.value.some(
      (candidate) => candidate.id === id && candidate.username === user.value?.username
    )

    await api.resetAccountOtp(id)

    if (changesCurrentUser) {
      await refresh()
    }

    await loadUsers()
  }

  async function removeUser(id: string): Promise<void> {
    await api.deleteAccount(id)
    await loadUsers()
  }

  function clear(): void {
    user.value = null
    users.value = []
    environment.clearSensitive()
  }

  return {
    signOutEverywhere,
    requireSignIn,
    requireOtp,
    hasUsers,
    setupCodeRequired,
    unisolatedBuildsAllowed,
    user,
    users,
    needsSignIn,
    needsOtpSetup,
    isAdmin,
    canWrite,
    refresh,
    signIn,
    verifyOtp,
    setupAccount,
    signOut,
    loadUsers,
    createUser,
    updateUser,
    resetPassword,
    resetOtp,
    removeUser,
    clear
  }
})
