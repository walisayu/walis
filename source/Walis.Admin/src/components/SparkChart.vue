<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Chart, registerables } from 'chart.js'

Chart.register(...registerables)

const props = defineProps<{ values: number[]; kind?: 'line' | 'bar'; fill?: boolean }>()

const canvas = ref<HTMLCanvasElement | null>(null)
let chart: Chart | null = null

function build(): void {
  if (!canvas.value) {
    return
  }

  chart?.destroy()

  chart = new Chart(canvas.value, {
    type: props.kind ?? 'line',
    data: {
      labels: props.values.map((_, index) => String(index)),
      datasets: [
        {
          backgroundColor: props.fill || props.kind === 'bar' ? 'rgba(255,255,255,.2)' : 'transparent',
          borderColor: 'rgba(255,255,255,.55)',
          pointBackgroundColor: 'rgba(255,255,255,.85)',
          data: props.values,
          fill: props.fill ?? false,
          barPercentage: 0.6
        }
      ]
    },
    options: {
      maintainAspectRatio: false,
      plugins: { legend: { display: false }, tooltip: { enabled: false } },
      scales: { x: { display: false }, y: { display: false, beginAtZero: true } },
      elements: {
        line: { borderWidth: 2, tension: 0.4 },
        point: { radius: 0, hitRadius: 10, hoverRadius: 0 }
      }
    }
  })
}

onMounted(build)
onBeforeUnmount(() => chart?.destroy())
watch(() => props.values, build, { deep: true })
</script>

<template>
  <div class="c-chart-wrapper mt-3 mx-3" style="height: 70px">
    <canvas ref="canvas" class="chart" height="70"></canvas>
  </div>
</template>
