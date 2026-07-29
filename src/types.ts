export interface EnvVariable {
  key: string
  value: string
}

export interface ProjectConfig {
  id: string
  name: string
  directory: string
  groupId: string | null
  command: string
  env: EnvVariable[]
  port: number | null
  tasks: ProjectTask[]
}

export interface ProjectGroup {
  id: string
  name: string
  collapsed: boolean
}

export type TaskMode = 'service' | 'once'

export interface ProjectTask {
  id: string
  name: string
  command: string
  mode: TaskMode
}

export type ProcessState = 'starting' | 'running' | 'stopping' | 'stopped' | 'succeeded' | 'failed'

export interface RuntimeStatus {
  runId: string
  projectId: string
  taskId: string
  taskName: string
  mode: TaskMode
  state: ProcessState
  pid: number | null
  startedAt: number | null
  exitCode: number | null
}

export type LogStream = 'stdout' | 'stderr' | 'system'

export interface LogEntry {
  id: string
  runId: string
  projectId: string
  stream: LogStream
  message: string
  timestamp: number
  transient?: boolean
}

