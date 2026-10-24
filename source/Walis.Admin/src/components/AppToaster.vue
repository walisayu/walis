<script setup lang="ts">
import { useToastStore } from '../stores/toasts'

const toasts = useToastStore()
</script>

<template>
  <CToaster class="p-3" placement="bottom-end">
    <CToast
      v-for="toast in toasts.items"
      :key="toast.id"
      :color="toast.color"
      class="text-white align-items-center"
      visible
      @close="toasts.remove(toast.id)"
    >
      <div class="d-flex">
        <CToastBody>{{ toast.message }}</CToastBody>
        <CToastClose class="me-2 m-auto" white />
      </div>
    </CToast>
  </CToaster>
</template>
