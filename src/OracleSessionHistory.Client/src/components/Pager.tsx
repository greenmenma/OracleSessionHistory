export default function Pager({
  page,
  pageSize,
  total,
  onChange,
}: {
  page: number
  pageSize: number
  total: number
  onChange: (p: number) => void
}) {
  const last = Math.max(1, Math.ceil(total / pageSize))
  return (
    <div className="pager">
      <button disabled={page <= 1} onClick={() => onChange(1)}>
        «
      </button>
      <button disabled={page <= 1} onClick={() => onChange(page - 1)}>
        ‹
      </button>
      <span>
        {page} / {last}
      </span>
      <button disabled={page >= last} onClick={() => onChange(page + 1)}>
        ›
      </button>
      <button disabled={page >= last} onClick={() => onChange(last)}>
        »
      </button>
    </div>
  )
}
