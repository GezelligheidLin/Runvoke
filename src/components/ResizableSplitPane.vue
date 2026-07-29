<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

const props = withDefaults(defineProps<{
  initialStartSize?: number
  minStartSize?: number
  minEndSize?: number
  maxStartSize?: number
  storageKey?: string
  label?: string
  disabled?: boolean
}>(), {
  initialStartSize: 292,
  minStartSize: 220,
  minEndSize: 520,
  maxStartSize: Number.POSITIVE_INFINITY,
  storageKey: '',
  label: '调整分栏宽度',
  disabled: false,
})

const root = ref<HTMLElement | null>(null)
const startSize = ref(props.initialStartSize)
const preferredStartSize = ref(props.initialStartSize)
const availableMaxStartSize = ref(Number.isFinite(props.maxStartSize) ? props.maxStartSize : props.initialStartSize)
const resizing = ref(false)
const dividerSize = 1
let resizeObserver: ResizeObserver | null = null

const splitStyle = computed(() => ({
  '--split-start-size': `${startSize.value}px`,
  '--split-divider-size': `${dividerSize}px`,
}))

function readStoredSize() {
  if (!props.storageKey)
    return

  const storedSize = Number.parseFloat(localStorage.getItem(props.storageKey) ?? '')
  if (Number.isFinite(storedSize)) {
    preferredStartSize.value = storedSize
    startSize.value = storedSize
  }
}

function persistSize() {
  if (props.storageKey)
    localStorage.setItem(props.storageKey, String(Math.round(preferredStartSize.value)))
}

function clampSize(size: number) {
  const containerWidth = root.value?.getBoundingClientRect().width ?? 0
  if (!containerWidth)
    return size

  const maxByEndPane = containerWidth - dividerSize - props.minEndSize
  const maximum = Math.max(props.minStartSize, Math.min(props.maxStartSize, maxByEndPane))
  availableMaxStartSize.value = maximum
  return Math.min(maximum, Math.max(props.minStartSize, size))
}

function updateSize(size: number, remember = false) {
  const nextSize = clampSize(size)
  startSize.value = nextSize
  if (remember)
    preferredStartSize.value = nextSize
}

function handlePointerDown(event: PointerEvent) {
  if (event.button !== 0)
    return

  resizing.value = true
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  document.documentElement.classList.add('is-resizing-split-pane')
  event.preventDefault()
}

function handlePointerMove(event: PointerEvent) {
  if (!resizing.value || !root.value)
    return

  const bounds = root.value.getBoundingClientRect()
  updateSize(event.clientX - bounds.left - dividerSize / 2, true)
}

function finishResize(event: PointerEvent) {
  if (!resizing.value)
    return

  resizing.value = false
  const target = event.currentTarget as HTMLElement
  if (target.hasPointerCapture(event.pointerId))
    target.releasePointerCapture(event.pointerId)
  document.documentElement.classList.remove('is-resizing-split-pane')
  persistSize()
}

function handleKeydown(event: KeyboardEvent) {
  const step = event.shiftKey ? 32 : 12
  let nextSize: number | null = null

  if (event.key === 'ArrowLeft')
    nextSize = startSize.value - step
  else if (event.key === 'ArrowRight')
    nextSize = startSize.value + step
  else if (event.key === 'Home')
    nextSize = props.minStartSize
  else if (event.key === 'End')
    nextSize = availableMaxStartSize.value

  if (nextSize === null)
    return

  event.preventDefault()
  updateSize(nextSize, true)
  persistSize()
}

function handleWindowResize() {
  updateSize(preferredStartSize.value)
}

onMounted(() => {
  readStoredSize()
  updateSize(preferredStartSize.value)

  if (typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(handleWindowResize)
    if (root.value)
      resizeObserver.observe(root.value)
  }
  else {
    window.addEventListener('resize', handleWindowResize)
  }
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  window.removeEventListener('resize', handleWindowResize)
  document.documentElement.classList.remove('is-resizing-split-pane')
})
</script>

<template>
  <div ref="root" class="resizable-split-pane" :class="{ resizing, disabled }" :style="splitStyle">
    <div class="split-pane-start">
      <slot name="start" />
    </div>

    <div
      class="split-pane-divider"
      role="separator"
      aria-orientation="vertical"
      :aria-label="label"
      :aria-valuemin="Math.round(minStartSize)"
      :aria-valuemax="Math.round(availableMaxStartSize)"
      :aria-valuenow="Math.round(startSize)"
      tabindex="0"
      @pointerdown="handlePointerDown"
      @pointermove="handlePointerMove"
      @pointerup="finishResize"
      @pointercancel="finishResize"
      @keydown="handleKeydown"
    />

    <div class="split-pane-end">
      <slot name="end" />
    </div>
  </div>
</template>

<style scoped>
.resizable-split-pane {
  display: grid;
  min-width: 0;
  min-height: 0;
  grid-template-columns: var(--split-start-size) var(--split-divider-size) minmax(0, 1fr);
  overflow: hidden;
}

.resizable-split-pane.disabled {
  grid-template-columns: 0 0 minmax(0, 1fr);
}

.resizable-split-pane.disabled > .split-pane-start,
.resizable-split-pane.disabled > .split-pane-divider {
  width: 0;
  visibility: hidden;
  pointer-events: none;
}

.split-pane-start,
.split-pane-end {
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

.split-pane-divider {
  position: relative;
  z-index: 3;
  width: var(--split-divider-size);
  min-height: 0;
  border: 0;
  outline: 0;
  background: var(--canvas);
  cursor: col-resize;
  touch-action: none;
}

.split-pane-divider::before {
  position: absolute;
  top: 0;
  bottom: 0;
  left: 50%;
  width: 9px;
  content: "";
  transform: translateX(-50%);
}

.split-pane-divider::after {
  position: absolute;
  top: 0;
  bottom: 0;
  left: 50%;
  width: 1px;
  background: var(--line);
  content: "";
  transform: translateX(-50%);
  transition: width .16s ease, background .16s ease;
}

.split-pane-divider:hover::after,
.split-pane-divider:focus-visible::after,
.resizing .split-pane-divider::after {
  width: 2px;
  background: var(--accent);
}

.split-pane-divider:focus-visible {
  box-shadow: inset 0 0 0 2px var(--accent-soft);
}

:global(html.is-resizing-split-pane),
:global(html.is-resizing-split-pane *) {
  cursor: col-resize !important;
  user-select: none !important;
}

@media (max-width: 760px) {
  .resizable-split-pane {
    display: block;
    overflow: visible;
  }

  .split-pane-divider {
    display: none;
  }

  .split-pane-start,
  .split-pane-end {
    overflow: visible;
  }
}

@media (prefers-reduced-motion: reduce) {
  .split-pane-divider::after {
    transition-duration: .01ms;
  }
}
</style>
