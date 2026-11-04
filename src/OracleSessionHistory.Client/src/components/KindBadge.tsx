import { kindLabels } from '../format'

const colors: Record<string, string> = {
  Client: 'gray',
  DbLinkSuspect: 'amber',
  JobSlave: 'purple',
  ParallelSlave: 'blue',
  AqSlave: 'blue',
  DataPump: 'purple',
  OracleInternal: 'gray',
  Background: 'gray',
}

export default function KindBadge({ kind }: { kind: string }) {
  if (kind === 'Client') return null
  return <span className={`badge ${colors[kind] ?? 'gray'}`}>{kindLabels[kind] ?? kind}</span>
}
