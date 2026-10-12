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
}>(), {
  placeholder: '请选择',
  disabled: false,
})

const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const input = ref<HTMLInputElement | null>(null)
const open = ref(false)
const keyword = ref('')

const selected = computed(() => props.options.find((item) => item.value === props.modelValue))
const displayValue = computed(() => selected.value
  ? `${selected.value.value} ${selected.value.label}`
  : props.modelValue)
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
  if (!open.value) keyword.value = displayValue.value
}, { immediate: true })
watch(() => props.options, () => {
  if (!open.value) keyword.value = displayValue.value
})

async function showOptions(): Promise<void> {
  if (props.disabled) return
  open.value = true
  keyword.value = ''
  await nextTick()
  input.value?.focus()
}

function updateKeyword(event: Event): void {
  keyword.value = (event.target as HTMLInputElement).value
  if (keyword.value !== displayValue.value) emit('update:modelValue', '')
  open.value = true
}

function choose(option: SearchSelectOption): void {
  emit('update:modelValue', option.value)
  keyword.value = option.value ? `${option.value} ${option.label}` : ''
  open.value = false
}

function closeLater(): void {
  window.setTimeout(() => {
    open.value = false
    keyword.value = displayValue.value
  }, 120)
}
</script>

<template>
  <div class="search-select" :class="{ disabled }">
    <input
      ref="input"
      :value="keyword"
      type="search"
      autocomplete="off"
      :disabled="disabled"
      :placeholder="placeholder"
      @focus="showOptions"
      @input="updateKeyword"
      @blur="closeLater"
      @keydown.esc="open = false"
    />
    <button v-if="modelValue && !disabled" type="button" aria-label="清除" @mousedown.prevent @click="choose({ value: '', label: '' })">×</button>
    <div v-if="open" class="search-select-options">
      <button
        v-for="option in filteredOptions"
        :key="option.value"
        type="button"
        :class="{ selected: option.value === modelValue }"
        @mousedown.prevent
        @click="choose(option)"
      >
        <strong>{{ option.value }}</strong><span>{{ option.label }}</span>
      </button>
      <span v-if="!filteredOptions.length" class="search-select-empty">未找到</span>
    </div>
  </div>
</template>

<style scoped>
.search-select{position:relative;min-width:0}.search-select>input{width:100%;height:44px;padding:0 38px 0 11px;border:1px solid #d4deea;border-radius:10px;background:#fff;color:#17233d;font-size:14px;outline:none}.search-select>input:focus{border-color:#3378cf;box-shadow:0 0 0 3px rgba(51,120,207,.1)}.search-select>button{position:absolute;z-index:2;top:5px;right:5px;width:34px;height:34px;border:0;border-radius:8px;background:#f2f5f8;color:#77869a;font-size:20px}.search-select-options{position:absolute;z-index:35;top:calc(100% + 5px);right:0;left:0;max-height:230px;overflow:auto;border:1px solid #d4deea;border-radius:11px;background:#fff;box-shadow:0 10px 28px rgba(23,54,93,.16)}.search-select-options>button{width:100%;min-height:43px;display:flex;align-items:center;gap:8px;padding:7px 10px;border:0;border-bottom:1px solid #edf1f5;background:#fff;color:#42566d;text-align:left}.search-select-options>button:last-of-type{border-bottom:0}.search-select-options>button.selected{background:#edf4fd;color:#2368c4}.search-select-options strong{flex:0 0 auto;font:700 13px ui-monospace,SFMono-Regular,Consolas,monospace}.search-select-options span{overflow:hidden;font-size:13px;text-overflow:ellipsis;white-space:nowrap}.search-select-empty{min-height:48px;display:grid;place-items:center;color:#8b98a8;font-size:13px}.search-select.disabled>input{background:#f2f4f7;color:#8a94a2}
</style>
