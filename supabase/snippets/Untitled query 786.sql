create index if not exists idx_document_chunks_metadata_record_type
on document_chunks ((metadata->>'recordType'));

create index if not exists idx_document_chunks_metadata_nik
on document_chunks ((metadata->>'nik'));

create index if not exists idx_document_chunks_metadata_name_normalized
on document_chunks ((metadata->>'nameNormalized'));

create index if not exists idx_document_chunks_metadata_maintenance_code
on document_chunks ((metadata->>'maintenanceCode'));

create index if not exists idx_document_chunks_metadata_date
on document_chunks ((metadata->>'date'));

create index if not exists idx_document_chunks_metadata_division
on document_chunks ((metadata->>'division'));

create index if not exists idx_document_chunks_metadata_shift
on document_chunks ((metadata->>'shift'));

create index if not exists idx_document_chunks_metadata_employee_status
on document_chunks ((metadata->>'employeeStatus'));

create index if not exists idx_document_chunks_metadata_approval
on document_chunks ((metadata->>'approval'));

create index if not exists idx_document_chunks_metadata_location
on document_chunks ((metadata->>'location'));

create index if not exists idx_document_chunks_metadata_maintenance_status
on document_chunks ((metadata->>'maintenanceStatus'));

create index if not exists idx_document_chunks_metadata_technician
on document_chunks ((metadata->>'technician'));