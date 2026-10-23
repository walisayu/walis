<script setup lang="ts">
// A page's own action buttons (add, refresh, save, ...) all go to the right of the breadcrumb row in the header,
// leaving the card header with only the title and summary. The mount point is #page-actions in AppHeader.
</script>

<template>
  <Teleport to="#page-actions">
    <slot />
  </Teleport>
</template>
