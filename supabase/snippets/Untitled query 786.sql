select
  id,
  chunk_index,
  left(content, 80) as preview,
  metadata->>'recordType' as record_type,
  metadata->>'nik' as nik,
  metadata->>'maintenanceCode' as maintenance_code,
  metadata->>'documentTitle' as document_title
from document_chunks
order by chunk_index
limit 20;