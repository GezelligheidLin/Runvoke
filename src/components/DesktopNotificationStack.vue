<script lang="ts">
export type DesktopNotificationPosition =
  | 'top-left'
  | 'top-center'
  | 'top-right'
  | 'bottom-left'
  | 'bottom-center'
  | 'bottom-right'

export type DesktopNotificationDisplayMode = 'list' | 'stack'

export interface DesktopNotificationPayload {
  id: string
  tone?: 'success' | 'error'
  eyebrow?: string
  title: string
  message?: string
  meta?: string
  timeLabel?: string
  icon?: string
  iconAlt?: string
  duration?: number
}

export interface DesktopNotificationHandle {
  id: string
  count: number
  evictedId?: string
}
</script>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'

interface DesktopNotificationItem extends DesktopNotificationPayload {
  slot: number
  prepared: boolean
  visible: boolean
  paused: boolean
  closing: boolean
  remainingTime: number
}

const props = withDefaults(defineProps<{
  position: DesktopNotificationPosition
  maxNotifications?: number
  defaultDuration?: number
  exitAnimationMs?: number
  closeLabel?: string
  displayMode?: DesktopNotificationDisplayMode
  initiallyExpanded?: boolean
  expandLabel?: string
  collapseLabel?: string
}>(), {
  maxNotifications: 3,
  defaultDuration: 5_000,
  exitAnimationMs: 360,
  closeLabel: 'Close notification',
  displayMode: 'list',
  initiallyExpanded: false,
  expandLabel: 'Expand notifications',
  collapseLabel: 'Collapse notifications',
})

const emit = defineEmits<{
  dismissStart: [id: string, count: number]
  dismissed: [id: string, count: number]
  empty: []
  expandedChange: [expanded: boolean]
}>()

const notifications = ref<DesktopNotificationItem[]>([])
const timers = new Map<string, number>()
const timerStartedAt = new Map<string, number>()
const animationFrames = new Set<number>()
const count = computed(() => notifications.value.length)
const expanded = ref(props.initiallyExpanded)
const hoveredNotificationIds = new Set<string>()
let pointerInsideStack = false
let hoverReconcileTimer: number | undefined
let lastPointerPosition: { x: number, y: number } | undefined
const stackActive = computed(() => props.displayMode === 'stack' && notifications.value.length > 1)
const stackCollapsed = computed(() => stackActive.value && !expanded.value)

watch(() => props.displayMode, async (mode) => {
  if (mode !== 'stack')
    setExpanded(false)
  await nextTick()
  reconcilePauseState()
  scheduleHoverReconcile()
})

function nextAnimationFrame() {
  return new Promise<void>((resolve) => {
    const frame = window.requestAnimationFrame(() => {
      animationFrames.delete(frame)
      resolve()
    })
    animationFrames.add(frame)
  })
}

function clearTimer(id: string) {
  const timer = timers.get(id)
  if (timer !== undefined) {
    window.clearTimeout(timer)
    timers.delete(id)
  }
  timerStartedAt.delete(id)
}

function scheduleDismiss(item: DesktopNotificationItem) {
  clearTimer(item.id)
  timerStartedAt.set(item.id, performance.now())
  timers.set(item.id, window.setTimeout(() => dismiss(item.id), item.remainingTime))
}

function claimSlot() {
  for (let slot = 0; slot < props.maxNotifications; slot += 1) {
    if (!notifications.value.some((notification) => notification.slot === slot))
      return { slot }
  }

  const removed = notifications.value.shift()
  if (removed) {
    clearTimer(removed.id)
    return { slot: removed.slot, evictedId: removed.id }
  }
  return { slot: 0 }
}

async function add(notification: DesktopNotificationPayload): Promise<DesktopNotificationHandle> {
  const existing = notifications.value.find((item) => item.id === notification.id)
  if (existing)
    return { id: existing.id, count: notifications.value.length }

  const { slot, evictedId } = claimSlot()
  const duration = Math.max(0, notification.duration ?? props.defaultDuration)
  const item = reactive<DesktopNotificationItem>({
    ...notification,
    slot,
    prepared: false,
    visible: false,
    paused: false,
    closing: false,
    duration,
    remainingTime: duration,
  })
  notifications.value.push(item)
  await nextTick()
  item.prepared = true
  await nextTick()
  return { id: item.id, count: notifications.value.length, evictedId }
}

async function present(id: string) {
  const item = notifications.value.find((notification) => notification.id === id)
  if (!item || item.visible || item.closing)
    return false

  await nextAnimationFrame()
  await nextAnimationFrame()
  item.visible = true
  scheduleDismiss(item)
  reconcilePauseState()
  return true
}

function pause(item: DesktopNotificationItem) {
  if (item.paused || item.closing || !item.visible)
    return
  const startedAt = timerStartedAt.get(item.id) ?? performance.now()
  clearTimer(item.id)
  item.remainingTime = Math.max(0, item.remainingTime - (performance.now() - startedAt))
  item.paused = true
}

function resume(item: DesktopNotificationItem) {
  if (!item.paused || item.closing || item.remainingTime <= 0)
    return
  item.paused = false
  scheduleDismiss(item)
}

function reconcilePauseState() {
  const pauseEntireStack = stackCollapsed.value && pointerInsideStack
  notifications.value.forEach((item) => {
    const shouldPause = pauseEntireStack
      || (!stackCollapsed.value && hoveredNotificationIds.has(item.id))
    if (shouldPause)
      pause(item)
    else
      resume(item)
  })
}

function rememberPointer(event: MouseEvent) {
  lastPointerPosition = { x: event.clientX, y: event.clientY }
}

function refreshHoverStateFromLayout() {
  hoveredNotificationIds.clear()
  pointerInsideStack = false
  if (lastPointerPosition) {
    const target = document.elementFromPoint(lastPointerPosition.x, lastPointerPosition.y)
    const card = target?.closest<HTMLElement>('[data-notification-id]')
    const id = card?.dataset.notificationId
    if (id) {
      hoveredNotificationIds.add(id)
      pointerInsideStack = true
    }
  }
  reconcilePauseState()
}

function scheduleHoverReconcile() {
  if (hoverReconcileTimer !== undefined)
    window.clearTimeout(hoverReconcileTimer)
  hoverReconcileTimer = window.setTimeout(() => {
    hoverReconcileTimer = undefined
    refreshHoverStateFromLayout()
  }, props.exitAnimationMs + 24)
}

function handleStackMouseEnter(event: MouseEvent) {
  rememberPointer(event)
  pointerInsideStack = true
  reconcilePauseState()
}

function handleStackMouseLeave() {
  pointerInsideStack = false
  lastPointerPosition = undefined
  hoveredNotificationIds.clear()
  reconcilePauseState()
}

function handleCardMouseEnter(event: MouseEvent, item: DesktopNotificationItem) {
  rememberPointer(event)
  hoveredNotificationIds.add(item.id)
  reconcilePauseState()
}

function handleCardMouseLeave(item: DesktopNotificationItem) {
  hoveredNotificationIds.delete(item.id)
  reconcilePauseState()
}

function setExpanded(nextExpanded: boolean) {
  const normalized = props.displayMode === 'stack' && notifications.value.length > 1 && nextExpanded
  if (expanded.value === normalized)
    return
  expanded.value = normalized
  emit('expandedChange', normalized)
  reconcilePauseState()
  void nextTick(reconcilePauseState)
  scheduleHoverReconcile()
}

function toggleExpanded() {
  if (stackActive.value)
    setExpanded(!expanded.value)
}

function handleCardActivation(item: DesktopNotificationItem, isFront: boolean) {
  if (stackActive.value && isFront && !item.closing)
    setExpanded(!expanded.value)
}

function handleCardKeydown(event: KeyboardEvent, item: DesktopNotificationItem, isFront: boolean) {
  if (!stackActive.value || !isFront || item.closing)
    return
  if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault()
    setExpanded(!expanded.value)
  }
}

function dismiss(id: string) {
  const item = notifications.value.find((notification) => notification.id === id)
  if (!item || item.closing)
    return

  clearTimer(id)
  item.closing = true
  item.visible = false
  hoveredNotificationIds.delete(id)
  emit('dismissStart', id, notifications.value.length)
  window.setTimeout(() => {
    notifications.value = notifications.value.filter((notification) => notification.id !== id)
    emit('dismissed', id, notifications.value.length)
    if (notifications.value.length === 0)
      emit('empty')
    if (notifications.value.length <= 1)
      setExpanded(false)
    reconcilePauseState()
    scheduleHoverReconcile()
  }, props.exitAnimationMs)
}

onBeforeUnmount(() => {
  timers.forEach((timer) => window.clearTimeout(timer))
  animationFrames.forEach((frame) => window.cancelAnimationFrame(frame))
  timers.clear()
  animationFrames.clear()
  hoveredNotificationIds.clear()
  if (hoverReconcileTimer !== undefined)
    window.clearTimeout(hoverReconcileTimer)
})

defineExpose({ add, present, dismiss, count, expanded, toggleExpanded, setExpanded })
</script>

<template>
  <main
    class="system-notification-shell"
    :class="[
      `notification-position-${position}`,
      {
        visible: notifications.length > 0,
        'notification-stack-mode': displayMode === 'stack',
        'notification-stack-active': stackActive,
        'notification-stack-collapsed': stackCollapsed,
        'notification-stack-expanded': stackActive && expanded,
      },
    ]"
    @mouseenter="handleStackMouseEnter"
    @mousemove="rememberPointer"
    @mouseleave="handleStackMouseLeave"
  >
    <section
      v-for="(item, index) in notifications"
      :key="item.id"
      :data-notification-id="item.id"
      :style="{
        '--notification-offset': `${item.slot * 122}px`,
        '--notification-duration': `${item.duration ?? defaultDuration}ms`,
        '--notification-stack-depth': notifications.length - index - 1,
        '--notification-stack-collapse-top': `${(notifications.length - index - 1) * 8}px`,
        '--notification-stack-collapse-bottom': `${(notifications.length - index - 1) * -8}px`,
        '--notification-stack-expand-top': `${index * 122}px`,
        '--notification-stack-expand-bottom': `${index * -122}px`,
        '--notification-stack-scale': 1 - (notifications.length - index - 1) * 0.035,
        '--notification-stack-opacity': 1 - (notifications.length - index - 1) * 0.12,
        zIndex: index + 1,
      }"
      class="system-notification-card"
      :class="{
        prepared: item.prepared,
        visible: item.visible,
        paused: item.paused,
        'notification-tone-error': item.tone === 'error',
        'notification-stack-front': index === notifications.length - 1,
      }"
      role="status"
      aria-live="polite"
      :tabindex="stackActive && index === notifications.length - 1 ? 0 : undefined"
      :aria-expanded="stackActive && index === notifications.length - 1 ? expanded : undefined"
      @click="handleCardActivation(item, index === notifications.length - 1)"
      @keydown="handleCardKeydown($event, item, index === notifications.length - 1)"
      @mouseenter="handleCardMouseEnter($event, item)"
      @mouseleave="handleCardMouseLeave(item)"
    >
      <slot name="notification" :notification="item" :dismiss="dismiss">
        <img v-if="item.icon" :src="item.icon" :alt="item.iconAlt ?? ''" :aria-hidden="!item.iconAlt" />
        <div v-else class="system-notification-icon-placeholder" aria-hidden="true" />
        <div class="system-notification-content">
          <header><span>{{ item.eyebrow }}</span><time>{{ item.timeLabel }}</time></header>
          <strong>{{ item.title }}</strong>
          <p v-if="item.message">{{ item.message }}</p>
          <footer v-if="item.meta"><i aria-hidden="true" /> {{ item.meta }}</footer>
        </div>
        <button type="button" :aria-label="closeLabel" :title="closeLabel" @click.stop="dismiss(item.id)">×</button>
        <span class="system-notification-timer" aria-hidden="true"><i /></span>
      </slot>
      <button
        v-if="stackActive && index === notifications.length - 1"
        class="system-notification-stack-toggle"
        type="button"
        :aria-label="expanded ? collapseLabel : expandLabel"
        :title="expanded ? collapseLabel : expandLabel"
        :aria-expanded="expanded"
        @click.stop="toggleExpanded"
      >
        <i aria-hidden="true" />
      </button>
    </section>
  </main>
</template>

<style>
.system-notification-shell {
  --notification-screen-gutter: 18px;
  --notification-screen-gutter-half: 9px;
  --desktop-notification-surface: var(--surface, #fffdf9);
  --desktop-notification-surface-muted: var(--surface-muted, #f5f1eb);
  --desktop-notification-line: var(--line, #ded8cf);
  --desktop-notification-line-strong: var(--line-strong, #cbc3b8);
  --desktop-notification-ink: var(--ink, #302d29);
  --desktop-notification-muted: var(--muted, #766f66);
  --desktop-notification-subtle: var(--subtle, #9a9288);
  --desktop-notification-accent: var(--accent, #637e68);
  --desktop-notification-accent-strong: var(--accent-strong, #4f6955);
  --desktop-notification-accent-soft: var(--accent-soft, #e5ede3);
  --desktop-notification-danger: var(--danger, #a9564d);
  --desktop-notification-danger-soft: var(--danger-soft, #f8e9e6);
  position: relative;
  width: 100%;
  height: 100%;
  padding: 8px 10px 10px;
  opacity: 0;
  pointer-events: none;
  transition: opacity .18s ease;
  user-select: none;
}

.system-notification-shell.visible { opacity: 1; }
.system-notification-card {
  position: absolute;
  right: calc(10px + var(--notification-screen-gutter-half));
  left: calc(10px + var(--notification-screen-gutter-half));
  display: grid;
  height: 114px;
  visibility: hidden;
  grid-template-columns: 42px minmax(0, 1fr) 24px;
  align-items: start;
  gap: 11px;
  padding: 14px 12px 12px 15px;
  overflow: hidden;
  border: 1px solid color-mix(in srgb, var(--desktop-notification-line-strong) 78%, transparent);
  border-radius: 8px;
  color: var(--desktop-notification-ink);
  background: linear-gradient(135deg, color-mix(in srgb, var(--desktop-notification-accent-soft) 25%, var(--desktop-notification-surface)) 0%, var(--desktop-notification-surface) 48%, color-mix(in srgb, var(--desktop-notification-surface-muted) 72%, transparent) 100%);
  box-shadow: 0 14px 34px rgba(42,39,35,.14), 0 3px 9px rgba(42,39,35,.08);
  opacity: 0;
  transform: translate3d(0, 0, 0);
  transform-origin: center;
  transition: transform .36s cubic-bezier(.2,.86,.22,1), opacity .18s ease;
  will-change: transform;
}

.system-notification-card.prepared { visibility: visible; }
.system-notification-card.notification-tone-error {
  --desktop-notification-accent: var(--desktop-notification-danger);
  --desktop-notification-accent-strong: var(--desktop-notification-danger);
  --desktop-notification-accent-soft: var(--desktop-notification-danger-soft);
}
.notification-position-top-left .system-notification-card,
.notification-position-bottom-left .system-notification-card {
  right: 10px;
  left: calc(10px + var(--notification-screen-gutter));
}
.notification-position-top-right .system-notification-card,
.notification-position-bottom-right .system-notification-card {
  right: calc(10px + var(--notification-screen-gutter));
  left: 10px;
}
.notification-position-top-left .system-notification-card,
.notification-position-top-center .system-notification-card,
.notification-position-top-right .system-notification-card { top: calc(8px + var(--notification-screen-gutter) + var(--notification-offset)); }
.notification-position-bottom-left .system-notification-card,
.notification-position-bottom-center .system-notification-card,
.notification-position-bottom-right .system-notification-card { bottom: calc(10px + var(--notification-screen-gutter) + var(--notification-offset)); }
.notification-stack-mode.notification-position-top-left .system-notification-card,
.notification-stack-mode.notification-position-top-center .system-notification-card,
.notification-stack-mode.notification-position-top-right .system-notification-card { top: calc(8px + var(--notification-screen-gutter)); }
.notification-stack-mode.notification-position-bottom-left .system-notification-card,
.notification-stack-mode.notification-position-bottom-center .system-notification-card,
.notification-stack-mode.notification-position-bottom-right .system-notification-card { bottom: calc(10px + var(--notification-screen-gutter)); }
.notification-position-top-left .system-notification-card,
.notification-position-bottom-left .system-notification-card { transform: translate3d(calc(-100% - 28px), 0, 0); }
.notification-position-top-center .system-notification-card { transform: translate3d(0, calc(-100% - 26px), 0); }
.notification-position-top-right .system-notification-card,
.notification-position-bottom-right .system-notification-card { transform: translate3d(calc(100% + 28px), 0, 0); }
.notification-position-bottom-center .system-notification-card { transform: translate3d(0, calc(100% + 28px), 0); }
.system-notification-card.visible { opacity: 1; pointer-events: auto; transform: translate3d(0, 0, 0); }
.system-notification-card:not(.visible) { opacity: 0; pointer-events: none; }
.notification-stack-collapsed .system-notification-card {
  transform-origin: top center;
  transition: transform .28s cubic-bezier(.2,.86,.22,1), opacity .18s ease;
}
.notification-position-bottom-left.notification-stack-collapsed .system-notification-card,
.notification-position-bottom-center.notification-stack-collapsed .system-notification-card,
.notification-position-bottom-right.notification-stack-collapsed .system-notification-card {
  transform-origin: bottom center;
}
.notification-stack-collapsed .system-notification-card.visible {
  opacity: var(--notification-stack-opacity);
  pointer-events: none;
  transform: translate3d(0, var(--notification-stack-collapse-top), 0) scale(var(--notification-stack-scale));
}
.notification-position-bottom-left.notification-stack-collapsed .system-notification-card.visible,
.notification-position-bottom-center.notification-stack-collapsed .system-notification-card.visible,
.notification-position-bottom-right.notification-stack-collapsed .system-notification-card.visible {
  transform: translate3d(0, var(--notification-stack-collapse-bottom), 0) scale(var(--notification-stack-scale));
}
.notification-position-top-left.notification-stack-expanded .system-notification-card.visible,
.notification-position-top-center.notification-stack-expanded .system-notification-card.visible,
.notification-position-top-right.notification-stack-expanded .system-notification-card.visible {
  transform: translate3d(0, var(--notification-stack-expand-top), 0);
}
.notification-position-bottom-left.notification-stack-expanded .system-notification-card.visible,
.notification-position-bottom-center.notification-stack-expanded .system-notification-card.visible,
.notification-position-bottom-right.notification-stack-expanded .system-notification-card.visible {
  transform: translate3d(0, var(--notification-stack-expand-bottom), 0);
}
.notification-stack-collapsed .system-notification-card.notification-stack-front {
  cursor: pointer;
  pointer-events: auto;
}
.system-notification-card > img,
.system-notification-icon-placeholder {
  width: 38px;
  height: 38px;
  padding: 5px;
  border: 1px solid var(--desktop-notification-line);
  border-radius: 5px;
  background: var(--desktop-notification-surface-muted);
  object-fit: contain;
}
.system-notification-content { min-width: 0; }
.system-notification-content header { display: flex; align-items: center; justify-content: space-between; gap: 12px; color: var(--desktop-notification-subtle); font-family: "Cascadia Mono", Consolas, monospace; font-size: 8px; font-weight: 650; }
.system-notification-content header span { color: var(--desktop-notification-accent-strong); letter-spacing: .08em; }
.system-notification-content strong { display: block; margin-top: 5px; overflow: hidden; font-size: 12px; font-weight: 700; line-height: 1.3; text-overflow: ellipsis; white-space: nowrap; }
.system-notification-content p { margin: 4px 0 0; overflow: hidden; color: var(--desktop-notification-muted); font-size: 10px; line-height: 1.45; text-overflow: ellipsis; white-space: nowrap; }
.system-notification-content footer { display: flex; align-items: center; gap: 6px; margin-top: 7px; color: var(--desktop-notification-subtle); font-size: 8px; }
.system-notification-content footer i { width: 5px; height: 5px; border-radius: 50%; background: var(--desktop-notification-accent); box-shadow: 0 0 0 2px var(--desktop-notification-accent-soft); }
.system-notification-card > button { display: grid; width: 24px; height: 24px; place-items: center; padding: 0; border: 1px solid transparent; border-radius: 3px; color: var(--desktop-notification-subtle); background: transparent; cursor: pointer; font-size: 16px; line-height: 1; transition: border-color .16s ease, color .16s ease, background .16s ease; }
.system-notification-card > button:hover { border-color: var(--desktop-notification-line); color: var(--desktop-notification-ink); background: var(--desktop-notification-surface-muted); }
.system-notification-card > .system-notification-stack-toggle {
  position: absolute;
  right: 43px;
  bottom: 8px;
  display: none;
  width: 22px;
  height: 18px;
}
.notification-stack-active .system-notification-card > .system-notification-stack-toggle { display: grid; }
.system-notification-stack-toggle i {
  width: 6px;
  height: 6px;
  border-right: 1.5px solid currentColor;
  border-bottom: 1.5px solid currentColor;
  transform: translateY(-1px) rotate(45deg);
  transition: transform .18s ease;
}
.notification-stack-expanded .system-notification-stack-toggle i { transform: translateY(2px) rotate(225deg); }
.system-notification-timer { position: absolute; right: 0; bottom: 0; left: 3px; height: 2px; overflow: hidden; background: color-mix(in srgb, var(--desktop-notification-accent-soft) 52%, transparent); }
.system-notification-timer i { display: block; width: 100%; height: 100%; background: var(--desktop-notification-accent); transform-origin: left; }
.system-notification-card.visible .system-notification-timer i { animation: desktop-notification-countdown var(--notification-duration, 5s) linear forwards; }
.system-notification-card.paused .system-notification-timer i { animation-play-state: paused; }

@keyframes desktop-notification-countdown { from { transform: scaleX(1); } to { transform: scaleX(0); } }

@media (prefers-reduced-motion: reduce) {
  .system-notification-shell { transition-duration: .01ms; }
  .system-notification-card { transform: none !important; transition-duration: .01ms; }
}
</style>
