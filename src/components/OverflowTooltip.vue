<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Tooltip, TooltipContent, TooltipTrigger } from './ui/tooltip'

const props = withDefaults(defineProps<{
  text: string
  as?: string
  side?: 'top' | 'right' | 'bottom' | 'left'
}>(), {
  as: 'span',
  side: 'top',
})

const trigger = ref<HTMLElement | null>(null)
const truncated = ref(false)
let observer: ResizeObserver | undefined

function updateTruncation() {
  const element = trigger.value
  truncated.value = Boolean(element && (
    element.scrollWidth > element.clientWidth + 1
    || element.scrollHeight > element.clientHeight + 1
  ))
}

onMounted(() => {
  observer = new ResizeObserver(updateTruncation)
  if (trigger.value)
    observer.observe(trigger.value)
  void nextTick(updateTruncation)
})

onBeforeUnmount(() => observer?.disconnect())

watch(() => props.text, () => void nextTick(updateTruncation))
</script>

<template>
  <Tooltip>
    <TooltipTrigger as-child>
      <component :is="as" ref="trigger" class="overflow-tooltip-trigger">
        <slot />
      </component>
    </TooltipTrigger>
    <TooltipContent v-if="truncated" class="app-tooltip-content" :side="side" :side-offset="6">
      {{ text }}
    </TooltipContent>
  </Tooltip>
</template>
