<script setup lang="ts">
import { computed, ref } from 'vue'

const props = defineProps<{
  modelValue: string
  ariaLabel?: string
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const open = ref(false)
const visibleMonth = ref(new Date())

const monthTitle = computed(
  () => `${visibleMonth.value.getFullYear()}年${visibleMonth.value.getMonth() + 1}月`,
)

const calendarDays = computed(() => {
  const year = visibleMonth.value.getFullYear()
  const month = visibleMonth.value.getMonth()
  const firstDay = new Date(year, month, 1)
  const start = new Date(year, month, 1 - firstDay.getDay())

  return Array.from({ length: 42 }, (_, index) => {
    const value = new Date(start)
    value.setDate(start.getDate() + index)
    return {
      date: formatDate(value),
      day: value.getDate(),
      currentMonth: value.getMonth() === month,
    }
  })
})

function parseDate(value: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  if (!match) return null
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
  return Number.isNaN(date.getTime()) ? null : date
}

function formatDate(value: Date): string {
  const year = value.getFullYear()
  const month = String(value.getMonth() + 1).padStart(2, '0')
  const day = String(value.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

function showPicker(): void {
  const selected = parseDate(props.modelValue)
  const today = new Date()
  visibleMonth.value = selected
    ? new Date(selected.getFullYear(), selected.getMonth(), 1)
    : new Date(today.getFullYear(), today.getMonth(), 1)
  open.value = true
}

function moveMonth(offset: number): void {
  visibleMonth.value = new Date(
    visibleMonth.value.getFullYear(),
    visibleMonth.value.getMonth() + offset,
    1,
  )
}

function selectDate(value: string): void {
  emit('update:modelValue', value)
  open.value = false
}

function selectToday(): void {
  selectDate(formatDate(new Date()))
}
</script>

<template>
  <button
    class="date-field"
    type="button"
    :aria-label="ariaLabel"
    :aria-expanded="open"
    @click="showPicker"
  >
    <span>{{ modelValue || '请选择' }}</span>
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M7 3v3m10-3v3M4 9h16M5 5h14a1 1 0 0 1 1 1v13a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1Z" />
    </svg>
  </button>

  <Teleport to="body">
    <div v-if="open" class="date-mask" role="presentation" @click.self="open = false">
      <section class="date-panel" role="dialog" aria-modal="true" :aria-label="ariaLabel || '选择日期'">
        <header>
          <button type="button" aria-label="上个月" @click="moveMonth(-1)">‹</button>
          <strong>{{ monthTitle }}</strong>
          <button type="button" aria-label="下个月" @click="moveMonth(1)">›</button>
        </header>
        <div class="weekdays" aria-hidden="true">
          <span v-for="name in ['日', '一', '二', '三', '四', '五', '六']" :key="name">{{ name }}</span>
        </div>
        <div class="days">
          <button
            v-for="item in calendarDays"
            :key="item.date"
            type="button"
            :class="{ muted: !item.currentMonth, selected: item.date === modelValue }"
            @click="selectDate(item.date)"
          >
            {{ item.day }}
          </button>
        </div>
        <footer>
          <button type="button" @click="open = false">取消</button>
          <button type="button" @click="selectToday">今天</button>
        </footer>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.date-field{width:100%;height:43px;display:flex;align-items:center;justify-content:space-between;gap:8px;padding:0 11px;border:1px solid #d4deea;border-radius:10px;background:#fff;color:#263a54;font:inherit;text-align:left}.date-field:focus-visible{border-color:#3378cf;box-shadow:0 0 0 3px rgba(51,120,207,.1);outline:0}.date-field svg{width:19px;height:19px;flex:0 0 auto;fill:none;stroke:#66809f;stroke-linecap:round;stroke-linejoin:round;stroke-width:1.8}.date-mask{position:fixed;z-index:100;inset:0;display:flex;align-items:flex-end;justify-content:center;padding:14px;background:rgba(20,32,48,.42)}.date-panel{width:min(100%,420px);padding:16px;border-radius:18px;background:#fff;box-shadow:0 18px 44px rgba(15,29,48,.24)}.date-panel header{height:42px;display:grid;grid-template-columns:42px 1fr 42px;align-items:center}.date-panel header strong{text-align:center;color:#243a57;font-size:16px}.date-panel button{border:0;background:transparent;color:#36536f;font:inherit}.date-panel header button{height:40px;border-radius:10px;background:#f1f5f9;font-size:28px;line-height:1}.weekdays,.days{display:grid;grid-template-columns:repeat(7,1fr)}.weekdays{margin-top:8px;color:#8996a6;font-size:12px;text-align:center}.weekdays span{padding:7px 0}.days{gap:3px}.days button{aspect-ratio:1;border-radius:10px;font-size:14px}.days button.muted{color:#b8c1cc}.days button.selected{background:#2368c4;color:#fff;font-weight:750}.date-panel footer{display:flex;justify-content:flex-end;gap:8px;margin-top:10px}.date-panel footer button{height:40px;min-width:72px;border-radius:10px;background:#eef3f8;color:#49627e;font-weight:700}.date-panel footer button:last-child{background:#2368c4;color:#fff}
</style>
