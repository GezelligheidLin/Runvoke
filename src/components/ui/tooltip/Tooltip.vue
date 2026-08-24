<script setup lang="ts">
import { getCurrentInstance, onBeforeUnmount, onMounted, ref, watch } from "vue"
import type { TooltipRootEmits, TooltipRootProps } from "reka-ui"
import { TooltipRoot, useForwardProps } from "reka-ui"

const props = defineProps<TooltipRootProps>()
const emits = defineEmits<TooltipRootEmits>()

const forwarded = useForwardProps(props)
const openControlled = Object.prototype.hasOwnProperty.call(getCurrentInstance()?.vnode.props ?? {}, "open")
const open = ref(openControlled ? props.open : props.defaultOpen)
let mounted = false

function updateOpen(value: boolean) {
  if (!openControlled)
    open.value = value
  emits("update:open", value)
}

function closeTooltip() {
  if (open.value)
    updateOpen(false)
}

function handlePointerDown(event: PointerEvent) {
  const target = event.target
  if (target instanceof Element && target.closest('[role="tooltip"]'))
    return
  closeTooltip()
}

function handleVisibilityChange() {
  if (document.visibilityState !== "visible")
    closeTooltip()
}

function addDismissListeners() {
  document.addEventListener("pointerdown", handlePointerDown, true)
  document.addEventListener("scroll", closeTooltip, true)
  document.addEventListener("visibilitychange", handleVisibilityChange)
  window.addEventListener("blur", closeTooltip)
  window.addEventListener("resize", closeTooltip)
}

function removeDismissListeners() {
  document.removeEventListener("pointerdown", handlePointerDown, true)
  document.removeEventListener("scroll", closeTooltip, true)
  document.removeEventListener("visibilitychange", handleVisibilityChange)
  window.removeEventListener("blur", closeTooltip)
  window.removeEventListener("resize", closeTooltip)
}

watch(() => props.open, (value) => {
  if (openControlled)
    open.value = value
})

watch(open, (value) => {
  if (!mounted)
    return
  if (value)
    addDismissListeners()
  else
    removeDismissListeners()
})

onMounted(() => {
  mounted = true
  if (open.value)
    addDismissListeners()
})

onBeforeUnmount(() => {
  mounted = false
  removeDismissListeners()
})
</script>

<template>
  <TooltipRoot
    v-bind="forwarded"
    :open="open"
    @update:open="updateOpen"
  >
    <slot />
  </TooltipRoot>
</template>
