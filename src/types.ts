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

export interface ImportedProject {
  name: string
  directory: string
  source: string
  suggestedCommand: string | null
}

export type ProjectImportSource = 'vscode' | 'cursor'

export type NotificationPosition =
  | 'top-left'
  | 'top-center'
  | 'top-right'
  | 'bottom-left'
  | 'bottom-center'
  | 'bottom-right'

export interface McpServerStatus {
  enabled: boolean
  running: boolean
  port: number
  endpoint: string
  authorizationToken: string
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

