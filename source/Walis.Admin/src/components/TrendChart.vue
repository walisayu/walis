<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Chart, registerables } from 'chart.js'
import type { DailyBuildStat } from '../types'
import { useFormat } from '../composables/useFormat'

Chart.register(...registerables)

const props = defineProps<{ trend: DailyBuildStat[] }>()

const { t } = useI18n()
const { day } = useFormat()
const canvas = ref<HTMLCanvasElement | null>(null)
let chart: Chart | null = null

function style(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

function build(): void {
  if (!canvas.value) {
    return
  }

  chart?.destroy()

  chart = new Chart(canvas.value, {
    type: 'line',
    data: {
      labels: props.trend.map((item) => day(item.date)),
      datasets: [
        {
          label: t('建置次數'),
          backgroundColor: `rgba(${style('--cui-info-rgb')}, .1)`,
          borderColor: style('--cui-info'),
          pointHoverBackgroundColor: '#fff',
          borderWidth: 2,
          fill: true,
          data: props.trend.map((item) => item.total)
        },
        {
          label: t('成功'),
          borderColor: style('--cui-success'),
          pointHoverBackgroundColor: '#fff',
          borderWidth: 2,
          data: props.trend.map((item) => item.succeeded)
        },
        {
          label: t('失敗'),
          borderColor: style('--cui-danger'),
          pointHoverBackgroundColor: '#fff',
          borderWidth: 2,
          data: props.trend.map((item) => item.failed)
        }
      ]
    },
    options: {
      maintainAspectRatio: false,
      plugins: { legend: { display: true, position: 'bottom' } },
      scales: {
        x: {
          grid: { color: style('--cui-border-color-translucent'), drawOnChartArea: false },
          ticks: { color: style('--cui-body-color') }
        },
        y: {
          beginAtZero: true,
          border: { color: style('--cui-border-color-translucent') },
          grid: { color: style('--cui-border-color-translucent') },
          ticks: { color: style('--cui-body-color'), precision: 0, maxTicksLimit: 6 }
        }
      },
      elements: {
        line: { tension: 0.4 },
        point: { radius: 0, hitRadius: 10, hoverRadius: 4, hoverBorderWidth: 3 }
      }
    }
  })
}

onMounted(() => {
  build()
  document.documentElement.addEventListener('ColorSchemeChange', build)
})

onBeforeUnmount(() => {
  document.documentElement.removeEventListener('ColorSchemeChange', build)
  chart?.destroy()
})

watch(() => props.trend, build, { deep: true })
</script>

<template>
  <div class="c-chart-wrapper" style="height: 300px; margin-top: 2rem">
    <canvas ref="canvas" class="chart"></canvas>
  </div>
</template>
