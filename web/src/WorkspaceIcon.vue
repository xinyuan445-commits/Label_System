<script setup lang="ts">
import type { WorkspaceIcon } from '@/workspace'

defineProps<{ icon: WorkspaceIcon }>()

const iconPaths: Record<WorkspaceIcon, string[]> = {
  receive: [
    'M5 8.5 12 5l7 3.5v7L12 19l-7-3.5v-7Z',
    'M12 12v7M5.5 8.7l6.5 3.3 6.5-3.3M12 3v5M9.5 5.5 12 8l2.5-2.5',
  ],
  truck: [
    'M3 6h11v10H3V6ZM14 10h3l3 3v3h-6v-6Z',
    'M7 19a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM17 19a2 2 0 1 0 0-4 2 2 0 0 0 0 4Z',
  ],
  warehouse: [
    'm3 9 9-5 9 5v11H3V9ZM7 12h10v8H7v-8Z',
    'M7 15h10M10 12v8M14 12v8',
  ],
  document: ['M6 3h8l4 4v14H6V3ZM14 3v5h4', 'M9 12h6M9 16h4'],
  dispatch: [
    'M4 8.5 11 5l7 3.5v7L11 19l-7-3.5v-7ZM11 12v7M4.5 8.7 11 12l6.5-3.3',
    'M16 4h5v5M21 4l-6 6',
  ],
  material: ['M4 7h16v12H4V7ZM4 11h16M9 11v8', 'M14 4h6v6M20 4l-7 7'],
  transfer: ['M7 7h12M16 4l3 3-3 3M17 17H5M8 14l-3 3 3 3'],
  location: [
    'M12 21s6-5.1 6-11a6 6 0 1 0-12 0c0 5.9 6 11 6 11Z',
    'M12 13a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z',
  ],
  inventory: ['M8 4h8M9 3v3h6V3M6 5h12v16H6V5Z', 'm9 14 2 2 4-5'],
  approve: [
    'M12 3 20 6v6c0 5-3.4 8-8 9-4.6-1-8-4-8-9V6l8-3Z',
    'm8.5 12 2.2 2.2 4.8-5',
  ],
  query: [
    'M4 6h10v12H4V6ZM7 9h4M7 12h4M7 15h3',
    'M16.5 15.5a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM18.7 17.7 21 20',
  ],
}
</script>

<template>
  <svg class="workspace-icon" viewBox="0 0 24 24" aria-hidden="true">
    <path v-for="path in iconPaths[icon]" :key="path" :d="path" />
  </svg>
</template>

<style scoped>
.workspace-icon {
  width: 100%;
  height: 100%;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.7;
}
</style>
