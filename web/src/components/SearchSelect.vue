<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'

export interface SearchSelectOption {
  value: string
  label: string
}

const props = withDefaults(defineProps<{
  modelValue: string
  options: SearchSelectOption[]
  placeholder?: string
  disabled?: boolean
  labelOnly?: boolean
}>(), {
  placeholder: '请选择',
  disabled: false,
  labelOnly: false,
})

const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const searchInput = ref<HTMLInputElement | null>(null)
const open = ref(false)
const keyword = ref('')

const selected = computed(() => props.options.find((item) => item.value === props.modelValue))
const displayValue = computed(() => {
  if (!selected.value) return props.modelValue
  return props.labelOnly
    ? selected.value.label || selected.value.value
    : `${selected.value.value} ${selected.value.label}`.trim()
})
const filteredOptions = computed(() => {
  const value = keyword.value.trim().toLocaleLowerCase()
  const items = value
    ? props.options.filter((item) =>
        item.value.toLocaleLowerCase().includes(value) ||
        item.label.toLocaleLowerCase().includes(value))
    : props.options
  return items.slice(0, 80)
})

watch(() => props.modelValue, () => {
  if (!open.value) keyword.value = ''
})

async function showOptions(): Promise<void> {
  if (props.disabled) return
  open.value = true
  keyword.value = ''
  await nextTick()
  searchInput.value?.focus()
}

function choose(option: SearchSelectOption): void {
  emit('update:modelValue', option.value)
  open.value = false
  keyword.value = ''
}

function close(): void {
  open.value = false
  keyword.value = ''
}
</script>

<template>
  <div class="search-select" :class="{ disabled }">
    <button
      class="search-select-trigger"
      type="button"
      :disabled="disabled"
      @click="showOptions"
    >
      <span :class="{ placeholder: !displayValue }">{{ displayValue || placeholder }}</span>
      <i aria-hidden="true">⌄</i>
    </button>
    <button v-if="modelValue && !disabled" class="search-select-clear" type="button" aria-label="清除" @click.stop="choose({ value: '', label: '' })">×</button>

    <Teleport to="body">
      <div v-if="open" class="search-select-mask" @click.self="close">
        <section class="search-select-sheet" role="dialog" aria-modal="true" aria-label="选择">
          <header>
            <input
              ref="searchInput"
              v-model="keyword"
              type="search"
              autocomplete="off"
              enterkeyhint="search"
              placeholder="输入编号或名称"
              @keydown.esc="close"
            />
            <button type="button" @click="close">关闭</button>
          </header>
          <div class="search-select-options">
            <button
              v-for="option in filteredOptions"
              :key="option.value"
              type="button"
              :class="{ selected: option.value === modelValue }"
              @click="choose(option)"
            >
              <template v-if="labelOnly"><span>{{ option.label || option.value }}</span></template>
              <template v-else><strong>{{ option.value }}</strong><span>{{ option.label }}</span></template>
            </button>
            <span v-if="!filteredOptions.length" class="search-select-empty">未找到</span>
          </div>
        </section>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.search-select{position:relative;min-width:0}.search-select-trigger{width:100%;height:44px;display:flex;align-items:center;justify-content:space-between;gap:8px;padding:0 38px 0 11px;border:1px solid #d4deea;border-radius:10px;background:#fff;color:#17233d;font-size:14px;text-align:left}.search-select-trigger span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.search-select-trigger span.placeholder{color:#8a97a8}.search-select-trigger i{position:absolute;right:13px;color:#77869a;font-style:normal}.search-select-clear{position:absolute;z-index:2;top:5px;right:5px;width:34px;height:34px;border:0;border-radius:8px;background:#f2f5f8;color:#77869a;font-size:20px}.search-select.disabled .search-select-trigger{background:#f2f4f7;color:#8a94a2}.search-select-mask{position:fixed;z-index:120;inset:0;display:flex;align-items:flex-start;justify-content:center;padding:calc(env(safe-area-inset-top) + 12px) 12px 12px;background:rgba(18,31,48,.44)}.search-select-sheet{width:min(100%,620px);height:min(72dvh,560px);display:flex;flex-direction:column;overflow:hidden;border-radius:16px;background:#fff;box-shadow:0 18px 46px rgba(16,31,51,.3)}.search-select-sheet header{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:9px;padding:12px;border-bottom:1px solid #e7edf4}.search-select-sheet input{width:100%;height:44px;padding:0 11px;border:1px solid #bfcfe1;border-radius:10px;color:#17233d;font-size:16px;outline:none}.search-select-sheet input:focus{border-color:#3378cf;box-shadow:0 0 0 3px rgba(51,120,207,.1)}.search-select-sheet header button{padding:0 13px;border:0;border-radius:10px;background:#edf4fd;color:#2368c4;font-weight:700}.search-select-options{min-height:0;flex:1;overflow-y:auto;overscroll-behavior:contain}.search-select-options>button{width:100%;min-height:48px;display:flex;align-items:center;gap:8px;padding:9px 13px;border:0;border-bottom:1px solid #edf1f5;background:#fff;color:#42566d;text-align:left}.search-select-options>button:last-of-type{border-bottom:0}.search-select-options>button.selected{background:#edf4fd;color:#2368c4}.search-select-options strong{flex:0 0 auto;font:700 13px ui-monospace,SFMono-Regular,Consolas,monospace}.search-select-options span{overflow:hidden;font-size:14px;text-overflow:ellipsis;white-space:nowrap}.search-select-empty{min-height:56px;display:grid;place-items:center;color:#8b98a8;font-size:13px}
@media(max-height:620px){.search-select-sheet{height:calc(100dvh - env(safe-area-inset-top) - 24px)}}
</style>
