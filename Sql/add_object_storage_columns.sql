alter table documents add column if not exists storage_bucket text;
alter table documents add column if not exists storage_object_key text;
alter table documents add column if not exists content_type text;
