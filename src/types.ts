export interface EnvVariable {
  key: string
  value: string
}

export interface ProjectConfig {
  id: string
  name: string
  directory: string
  command: string
  env: EnvVariable[]
  port: number | null
}

export type ProcessState = 'starting' | 'running' | 'stopping' | 'stopped'

export interface RuntimeStatus {
  projectId: string
  state: ProcessState
  pid: number | null
  startedAt: number | null
  exitCode: number | null
}

export type LogStream = 'stdout' | 'stderr' | 'system'

export interface LogEntry {
  id: string
  projectId: string
  stream: LogStream
  message: string
  timestamp: number
}

