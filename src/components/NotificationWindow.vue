<script setup lang="ts">
import { invoke } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import { nextTick, onBeforeUnmount, onMounted, ref, useTemplateRef } from 'vue'
import brandIcon from '../../src-tauri/icons/128x128.png'
import DesktopNotificationStack from './DesktopNotificationStack.vue'
import type {
  DesktopNotificationDisplayMode,
  DesktopNotificationHandle,
  DesktopNotificationPayload,
  DesktopNotificationPosition,
} from './DesktopNotificationStack.vue'

interface NotificationConfig {
  id?: string
  theme?: string
  position?: string
  stackingEnabled?: boolean
  prewarmed?: boolean
  tone?: 'success' | 'error'
  title?: string
  message?: string
  meta?: string
  dedupeKey?: string
}

interface NotificationStackExposed {
  add: (notification: DesktopNotificationPayload) => Promise<DesktopNotificationHandle>
  present: (id: string) => Promise<boolean>
}

interface HitRegion {
  x: number
  y: number
  width: number
  height: number
}

const MAX_NOTIFICATIONS = 3
const HIT_REGION_PADDING = 8
const RECENT_DEDUPE_WINDOW = 60_000
const config = (Reflect.get(window, '__RUNVOKE_NOTIFICATION__') ?? {}) as NotificationConfig
const currentTheme = ref<'light' | 'dark'>(config.theme === 'dark' ? 'dark' : 'light')
const currentPosition = ref<DesktopNotificationPosition>(normalizePosition(config.position))
const displayMode = ref<DesktopNotificationDisplayMode>('list')
const notificationStack = useTemplateRef<NotificationStackExposed>('notificationStack')
let stopListening: UnlistenFn | undefined
let closeWindowTimer: number | undefined
let hitRegionSyncTimer: number | undefined
let windowPosition: DesktopNotificationPosition | undefined
let windowPresented = false
let receiveQueue = Promise.resolve()
const deliveredNotificationKeys = new Map<string, number>()

function normalizePosition(position?: string): DesktopNotificationPosition {
  const positions: DesktopNotificationPosition[] = [
    'top-left',
    'top-center',
    'top-right',
    'bottom-left',
    'bottom-center',
    'bottom-right',
  ]
  return positions.includes(position as DesktopNotificationPosition)
    ? position as DesktopNotificationPosition
    : 'bottom-right'
}

function applyTheme(theme: string) {
  const isDark = theme === 'dark'
  document.documentElement.dataset.theme = isDark ? 'dark' : 'light'
  document.documentElement.classList.add('notification-document')
  document.body.classList.add('notification-body')
  document.body.classList.toggle('theme-dark', isDark)
}

function clearCloseWindowTimer() {
  if (closeWindowTimer !== undefined) {
    window.clearTimeout(closeWindowTimer)
    closeWindowTimer = undefined
  }
}

function clearHitRegionSyncTimer() {
  if (hitRegionSyncTimer !== undefined) {
    window.clearTimeout(hitRegionSyncTimer)
    hitRegionSyncTimer = undefined
  }
}

function paddedRegion(rect: Pick<DOMRect, 'left' | 'top' | 'right' | 'bottom'>): HitRegion {
  const left = Math.max(0, rect.left - HIT_REGION_PADDING)
  const top = Math.max(0, rect.top - HIT_REGION_PADDING)
  const right = Math.min(window.innerWidth, rect.right + HIT_REGION_PADDING)
  const bottom = Math.min(window.innerHeight, rect.bottom + HIT_REGION_PADDING)
  return { x: left, y: top, width: right - left, height: bottom - top }
}

function collectVisibleCardRegions(interactiveOnly = true) {
  return [...document.querySelectorAll<HTMLElement>('.system-notification-card.visible')]
    .filter((element) => !interactiveOnly || window.getComputedStyle(element).pointerEvents !== 'none')
    .map((element) => element.getBoundingClientRect())
    .map(paddedRegion)
    .filter((region) => region.width > 0 && region.height > 0)
}

function collectExpandedTargetRegions() {
  const topPosition = currentPosition.value.startsWith('top-')
  return [...document.querySelectorAll<HTMLElement>('.system-notification-card.visible')]
    .map((element) => {
      const translationProperty = topPosition
        ? '--notification-stack-expand-top'
        : '--notification-stack-expand-bottom'
      const translation = Number.parseFloat(element.style.getPropertyValue(translationProperty)) || 0
      const left = element.offsetLeft
      const top = element.offsetTop + translation
      return paddedRegion({
        left,
        top,
        right: left + element.offsetWidth,
        bottom: top + element.offsetHeight,
      })
    })
    .map((rect) => {
      const left = Math.max(0, rect.x)
      const top = Math.max(0, rect.y)
      const right = Math.min(window.innerWidth, rect.x + rect.width)
      const bottom = Math.min(window.innerHeight, rect.y + rect.height)
      return { x: left, y: top, width: right - left, height: bottom - top }
    })
    .filter((region) => region.width > 0 && region.height > 0)
}

async function syncHitRegions() {
  await nextTick()
  const regions = collectVisibleCardRegions()
  await invoke('set_notification_hit_regions', { regions })
}

async function restoreFullWindowRegion() {
  clearHitRegionSyncTimer()
  await invoke('set_notification_hit_regions', {
    regions: [{
      x: 0,
      y: 0,
      width: window.innerWidth,
      height: window.innerHeight,
    }],
  })
}

function scheduleHitRegionSync(delay = 380) {
  clearHitRegionSyncTimer()
  hitRegionSyncTimer = window.setTimeout(() => {
    hitRegionSyncTimer = undefined
    void syncHitRegions().catch(() => {})
  }, delay)
}

async function positionWindow() {
  if (windowPosition === currentPosition.value)
    return
  await invoke('resize_notification_window', {
    position: currentPosition.value,
    count: MAX_NOTIFICATIONS,
  })
  windowPosition = currentPosition.value
}

function notificationPayload(id: string): DesktopNotificationPayload {
  return {
    id,
    tone: 'success',
    eyebrow: 'RUNVOKE',
    title: '测试通知已送达',
    message: '这是一条由独立桌面窗口显示的自定义通知。',
    meta: '系统通知测试',
    timeLabel: '刚刚',
    icon: brandIcon,
    duration: 5_000,
  }
}

async function showNotification(notification: NotificationConfig) {
  const id = notification.dedupeKey || notification.id || crypto.randomUUID()
  clearCloseWindowTimer()
  currentTheme.value = notification.theme === 'dark' ? 'dark' : 'light'
  currentPosition.value = normalizePosition(notification.position)
  displayMode.value = notification.stackingEnabled ? 'stack' : 'list'
  applyTheme(currentTheme.value)

  await restoreFullWindowRegion()
  await positionWindow()
  const handle = await notificationStack.value?.add({
    ...notificationPayload(id),
    tone: notification.tone === 'error' ? 'error' : 'success',
    title: notification.title?.trim() || '测试通知已送达',
    message: notification.message?.trim() || '这是一条由独立桌面窗口显示的自定义通知。',
    meta: notification.meta?.trim() || '系统通知测试',
  })
  if (!handle)
    throw new Error('通知组件尚未就绪')

  if (!windowPresented) {
    await invoke('redraw_notification_window')
    windowPresented = true
  }

  await notificationStack.value?.present(id)
  scheduleHitRegionSync()
}

async function receiveNotification(notification: NotificationConfig) {
  const dedupeKey = notification.dedupeKey?.trim()
  if (dedupeKey) {
    const now = Date.now()
    for (const [key, timestamp] of deliveredNotificationKeys) {
      if (now - timestamp > RECENT_DEDUPE_WINDOW)
        deliveredNotificationKeys.delete(key)
    }
    if (deliveredNotificationKeys.has(dedupeKey)) {
      if (notification.id)
        await invoke('notification_received', { id: notification.id }).catch(() => {})
      return
    }
  }
  await showNotification(notification)
  if (dedupeKey)
    deliveredNotificationKeys.set(dedupeKey, Date.now())
  if (notification.id)
    await invoke('notification_received', { id: notification.id }).catch(() => {})
}

function queueNotification(notification: NotificationConfig) {
  const queued = receiveQueue.then(() => receiveNotification(notification))
  receiveQueue = queued.catch(() => {})
}

function handleDismissStart() {
  void restoreFullWindowRegion().catch(() => {})
}

function handleDismissed() {
  // Removing an item changes stack indexes and the remaining cards animate
  // into their new slots. Keep the transition region until that reflow ends;
  // cropping immediately would clip the moving cards to their old rectangles.
  scheduleHitRegionSync()
}

function handleExpandedChange(expanded: boolean) {
  // Expanding needs the destination card regions before Vue paints the first
  // transition frame. Collapsing can retain the larger expanded union until
  // the cards settle. Avoid restoring the complete transparent canvas here:
  // doing so causes a non-client redraw and briefly exposes the window behind.
  if (expanded) {
    const regions = collectExpandedTargetRegions()
    void invoke('set_notification_hit_regions', { regions }).catch(() => {})
  }
  scheduleHitRegionSync()
}

function handleEmpty() {
  clearCloseWindowTimer()
  clearHitRegionSyncTimer()
  void invoke('set_notification_hit_regions', { regions: [] })
  closeWindowTimer = window.setTimeout(() => {
    closeWindowTimer = undefined
    void invoke('close_notification_window').then(() => {
      windowPresented = false
    })
  }, 20)
}

onMounted(async () => {
  applyTheme(currentTheme.value)
  await invoke('set_notification_hit_regions', { regions: [] }).catch(() => {})
  stopListening = await listen<NotificationConfig>('notification-config', (event) => {
    queueNotification(event.payload)
  })
  const pending = await invoke<NotificationConfig[]>('notification_window_ready').catch(() => [])
  for (const notification of pending)
    queueNotification(notification)
})

onBeforeUnmount(() => {
  clearCloseWindowTimer()
  clearHitRegionSyncTimer()
  void invoke('set_notification_hit_regions', { regions: [] })
  deliveredNotificationKeys.clear()
  stopListening?.()
})
</script>

<template>
  <DesktopNotificationStack
    ref="notificationStack"
    :position="currentPosition"
    :display-mode="displayMode"
    :max-notifications="MAX_NOTIFICATIONS"
    close-label="关闭通知"
    expand-label="展开通知"
    collapse-label="收起通知"
    @dismiss-start="handleDismissStart"
    @dismissed="handleDismissed"
    @expanded-change="handleExpandedChange"
    @empty="handleEmpty"
  />
</template>
