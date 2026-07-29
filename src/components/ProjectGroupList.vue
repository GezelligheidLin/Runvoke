<script setup lang="ts">
import { computed, ref, useTemplateRef, watch } from 'vue'
import { useDraggable, type DraggableEvent, type UseDraggableOptions } from 'vue-draggable-plus'
import type { ProjectConfig } from '../types'

const props = defineProps<{
  groupId: string | null
  projects: ProjectConfig[]
}>()

const emit = defineEmits<{
  move: [payload: { projectId: string, groupId: string | null, targetIndex: number }]
}>()

defineSlots<{
  default: (props: { project: ProjectConfig }) => unknown
}>()

const container = useTemplateRef<HTMLElement>('container')
const sortableProjects = ref<ProjectConfig[]>([...props.projects])

watch(
  () => props.projects,
  projects => sortableProjects.value = [...projects],
  { flush: 'sync' },
)

const sortableOptions = computed<UseDraggableOptions<ProjectConfig>>(() => ({
  animation: 160,
  direction: 'vertical',
  draggable: '.project-item',
  group: { name: 'runvoke-projects', pull: true, put: true },
  forceFallback: true,
  fallbackClass: 'project-drag-fallback',
  fallbackOnBody: true,
  fallbackTolerance: 4,
  ghostClass: 'project-drag-ghost',
  chosenClass: 'project-drag-chosen',
  dragClass: 'project-dragging',
  emptyInsertThreshold: 20,
  scroll: true,
  scrollSensitivity: 48,
  scrollSpeed: 10,
  onEnd: handleDragEnd,
}))

useDraggable(container, sortableProjects, sortableOptions)

function handleDragEnd(event: DraggableEvent<ProjectConfig>) {
  const oldIndex = event.oldDraggableIndex ?? event.oldIndex
  const targetIndex = event.newDraggableIndex ?? event.newIndex
  if (event.from === event.to && oldIndex === targetIndex)
    return
  const projectId = event.item.dataset.projectId
  const targetGroupId = (event.to as HTMLElement).dataset.groupId || null
  if (!projectId || targetIndex === undefined)
    return
  emit('move', { projectId, groupId: targetGroupId, targetIndex })
}
</script>

<template>
  <div
    ref="container"
    class="project-group-items"
    :class="{ empty: !sortableProjects.length }"
    :data-group-id="groupId ?? ''"
  >
    <template v-for="project in sortableProjects" :key="project.id">
      <slot :project="project" />
    </template>
    <span v-if="!sortableProjects.length" class="group-drop-zone">拖入项目</span>
  </div>
</template>
