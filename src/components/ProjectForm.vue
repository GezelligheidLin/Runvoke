<script setup lang="ts">
import { ref, watch } from 'vue'
import { open } from '@tauri-apps/plugin-dialog'
import type { ProjectConfig } from '../types'

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

function emptyProject(): ProjectConfig {
  return {
    id: '',
    name: '',
    directory: '',
    command: 'pnpm dev',
    env: [],
    port: null,
  }
}

watch(
  () => [isOpen, project] as const,
  () => {
    if (!isOpen)
      return
    form.value = project
      ? { ...project, env: project.env.map((item) => ({ ...item })) }
      : emptyProject()
    browseError.value = ''
  },
  { immediate: true },
)

async function browseDirectory() {
  browseError.value = ''
  try {
    const selected = await open({ directory: true, multiple: false, title: '选择项目目录' })
    if (selected)
      form.value.directory = selected
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

function submit() {
  emit('save', {
    ...form.value,
    port: form.value.port || null,
    env: form.value.env.filter((item) => item.key.trim()),
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
              <span>项目名称</span>
              <input v-model.trim="form.name" required maxlength="60" placeholder="例如：管理后台" />
            </label>

            <label class="field">
              <span>项目目录</span>
              <div class="input-action">
                <input v-model.trim="form.directory" required placeholder="D:\develop\my-project" />
                <button type="button" @click="browseDirectory">浏览</button>
              </div>
              <small v-if="browseError" class="field-error">{{ browseError }}</small>
            </label>

            <div class="field-grid">
              <label class="field field-command">
                <span>启动命令</span>
                <input v-model.trim="form.command" required placeholder="pnpm dev" />
              </label>
              <label class="field field-port">
                <span>端口（可选）</span>
                <input v-model.number="form.port" type="number" min="1" max="65535" placeholder="5173" />
              </label>
            </div>

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
