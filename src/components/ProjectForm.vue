<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { open } from '@tauri-apps/plugin-dialog'
import type { ProjectConfig, ProjectTask } from '../types'

const { open: isOpen, project } = defineProps<{
  open: boolean
  project: ProjectConfig | null
}>()

const emit = defineEmits<{
  close: []
  save: [project: ProjectConfig]
}>()

const form = ref<ProjectConfig>(emptyProject())
const browseError = ref('')
const detectedName = ref<{ name: string, source: string } | null>(null)
const canAddTask = computed(() => form.value.tasks.length < 3)

function emptyProject(): ProjectConfig {
  return {
    id: '',
    name: '',
    directory: '',
    command: 'pnpm dev',
    env: [],
    port: null,
    tasks: [defaultTask()],
  }
}

function defaultTask(): ProjectTask {
  return { id: '', name: '开发服务器', command: 'pnpm dev', mode: 'service' }
}

watch(
  () => [isOpen, project] as const,
  () => {
    if (!isOpen)
      return
    form.value = project
      ? { ...project, env: project.env.map((item) => ({ ...item })), tasks: project.tasks.map((task) => ({ ...task })) }
      : emptyProject()
    browseError.value = ''
    detectedName.value = null
  },
  { immediate: true },
)

async function browseDirectory() {
  browseError.value = ''
  try {
    const selected = await open({ directory: true, multiple: false, title: '选择项目目录' })
    if (selected) {
      form.value.directory = selected
      const detected = await invoke<{ name: string, source: string }>('detect_project_name', { directory: selected })
      detectedName.value = detected
      if (!form.value.name.trim())
        form.value.name = detected.name
    }
  }
  catch (error) {
    browseError.value = String(error)
  }
}

function addEnvironmentVariable() {
  form.value.env.push({ key: '', value: '' })
}

function removeEnvironmentVariable(index: number) {
  form.value.env.splice(index, 1)
}

function addTask() {
  if (!canAddTask.value)
    return
  form.value.tasks.push(defaultTask())
}

function removeTask(index: number) {
  if (form.value.tasks.length > 1)
    form.value.tasks.splice(index, 1)
}

function submit() {
  emit('save', {
    ...form.value,
    command: form.value.tasks[0]?.command ?? '',
    port: form.value.port || null,
    env: form.value.env.filter((item) => item.key.trim()),
    tasks: form.value.tasks.map((task) => ({ ...task, name: task.name.trim(), command: task.command.trim() })),
  })
}
</script>

<template>
  <Teleport to="body">
    <Transition name="modal">
      <div v-if="isOpen" class="modal-layer" @mousedown.self="emit('close')">
        <form class="project-form" @submit.prevent="submit">
          <header class="form-header">
            <div>
              <span class="section-kicker">项目配置</span>
              <h2>{{ project ? '编辑项目' : '接入新项目' }}</h2>
            </div>
            <button class="icon-button" type="button" aria-label="关闭" @click="emit('close')">×</button>
          </header>

          <div class="form-scroll">
            <label class="field">
              <span>项目目录</span>
              <div class="input-action">
                <input v-model.trim="form.directory" required placeholder="D:\develop\my-project" />
                <button type="button" @click="browseDirectory">浏览</button>
              </div>
              <small v-if="browseError" class="field-error">{{ browseError }}</small>
              <small v-else-if="detectedName" class="detected-name">已从 {{ detectedName.source }} 识别：{{ detectedName.name }}</small>
            </label>

            <label class="field">
              <span>项目名称</span>
              <input v-model.trim="form.name" required maxlength="60" placeholder="选择目录后自动识别，也可手动填写" />
            </label>

            <div class="field-grid">
              <label class="field field-port">
                <span>端口（可选）</span>
                <input v-model.number="form.port" type="number" min="1" max="65535" placeholder="5173" />
              </label>
            </div>

            <section class="task-section">
              <div class="env-heading">
                <div>
                  <span>项目任务</span>
                  <small>最多 3 条；常驻服务可停止，一次任务结束后保留日志和结果</small>
                </div>
                <button class="text-button" type="button" :disabled="!canAddTask" @click="addTask">+ 添加任务（{{ form.tasks.length }}/3）</button>
              </div>
              <div class="task-list">
                <div v-for="(task, index) in form.tasks" :key="task.id || index" class="task-row">
                  <input v-model.trim="task.name" aria-label="任务名称" required placeholder="构建生产包" />
                  <select v-model="task.mode" :aria-label="`${task.name || '任务'}类型`">
                    <option value="service">常驻服务</option>
                    <option value="once">一次任务</option>
                  </select>
                  <input v-model.trim="task.command" aria-label="任务命令" required placeholder="pnpm build" />
                  <button type="button" aria-label="删除任务" :disabled="form.tasks.length === 1" @click="removeTask(index)">×</button>
                </div>
              </div>
            </section>

            <section class="env-section">
              <div class="env-heading">
                <div>
                  <span>环境变量</span>
                  <small>变量名 / 变量值</small>
                </div>
                <button class="text-button" type="button" @click="addEnvironmentVariable">+ 添加变量</button>
              </div>
              <div v-if="form.env.length" class="env-list">
                <div v-for="(_, index) in form.env" :key="index" class="env-row">
                  <input v-model.trim="form.env[index]!.key" aria-label="变量名" placeholder="NODE_ENV" />
                  <span>=</span>
                  <input v-model="form.env[index]!.value" aria-label="变量值" placeholder="development" />
                  <button type="button" aria-label="删除变量" @click="removeEnvironmentVariable(index)">×</button>
                </div>
              </div>
              <div v-else class="env-empty">尚未配置环境变量</div>
            </section>
          </div>

          <footer class="form-footer">
            <button class="button-ghost" type="button" @click="emit('close')">取消</button>
            <button class="button-primary" type="submit">保存项目</button>
          </footer>
        </form>
      </div>
    </Transition>
  </Teleport>
</template>
