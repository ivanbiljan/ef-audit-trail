-- =====================================================================================
-- Trigger-based audit capture: the one place audit.entries is written from.
--
--   * Catches everything that reaches a table: SaveChanges, ExecuteUpdate/ExecuteDelete, raw
--     SQL, psql, other services, FK cascades.
--   * Who and what come from audit.* session settings published by AuditSessionInterceptor.
--   * Column list, property names, PII patterns and JSON columns are NOT configured here —
--     AuditTriggerInstaller derives them from the EF model ([AuditIgnore], [AuditPii], jsonb)
--     and calls audit.enable() per table, so the attributes stay the single source of truth.
--
-- Idempotent: safe to run from an EF migration (migrationBuilder.Sql) and to re-run.
-- Target: PostgreSQL 16 (CREATE OR REPLACE TRIGGER needs 14+).
-- =====================================================================================

create schema if not exists audit;

-- -------------------------------------------------------------------------------------
-- Per-table configuration. Written only by audit.enable()/audit.disable().
--   columns: [{ "column": "name", "property": "Name", "kind": "value|json|timestamptz|timestamp",
--               "pii": null | [] | ["/deliveryContact/**", ...] }]   -- audited columns only
-- -------------------------------------------------------------------------------------
create table if not exists audit.tracked_entity (
    relation        regclass primary key,
    entity_type     text     not null unique,
    key_columns     text[]   not null,
    company_column  text,
    columns         jsonb    not null
);

-- -------------------------------------------------------------------------------------
-- Session context, set by the application (AuditSessionInterceptor). '' => null.
-- -------------------------------------------------------------------------------------
create or replace function audit.ctx(p_name text) returns text
language sql stable
as $$ select nullif(current_setting('audit.' || p_name, true), '') $$;

-- Publishes the session context from inside a statement's WHERE clause, so a SaveChanges command
-- keeps its statement count: EF reads rows affected by statement index. High cost: the planner
-- checks the key first, so this only runs for rows the statement will actually write, and it
-- always runs before their trigger.
create or replace function audit.publish(
    p_operation     text,
    p_actor_kind    text,
    p_actor_id      text,
    p_actor_display text,
    p_on_behalf_of  text,
    p_channel       text,
    p_ip_address    text,
    p_user_agent    text) returns boolean
language plpgsql volatile cost 1000
as $$
begin
    perform set_config('audit.operation', p_operation, false),
            set_config('audit.actor_kind', p_actor_kind, false),
            set_config('audit.actor_id', p_actor_id, false),
            set_config('audit.actor_display', p_actor_display, false),
            set_config('audit.on_behalf_of_customer_id', p_on_behalf_of, false),
            set_config('audit.channel', p_channel, false),
            set_config('audit.ip_address', p_ip_address, false),
            set_config('audit.user_agent', p_user_agent, false);

    return true;
end
$$;

-- -------------------------------------------------------------------------------------
-- Value formatting: match what System.Text.Json writes for the same CLR value, so readers
-- of changes see the same text the application would have serialized.
-- -------------------------------------------------------------------------------------

-- DateTime: 2026-11-01T10:00:00Z, 2026-11-01T10:00:00.5Z (fraction trimmed, as "O" without padding).
create or replace function audit.format_timestamp(p_value timestamp) returns text
language sql immutable
as $$
    select to_char(p_value, 'YYYY-MM-DD"T"HH24:MI:SS')
        || coalesce('.' || nullif(rtrim(to_char(p_value, 'US'), '0'), ''), '')
$$;

create or replace function audit.normalize(p_value jsonb, p_kind text) returns jsonb
language sql stable
as $$
    select case
        when p_value is null or p_value = 'null'::jsonb then null
        when p_kind = 'timestamptz'
            then to_jsonb(audit.format_timestamp((p_value #>> '{}')::timestamptz at time zone 'UTC') || 'Z')
        when p_kind = 'timestamp'
            then to_jsonb(audit.format_timestamp((p_value #>> '{}')::timestamp))
        else p_value
    end
$$;

create or replace function audit.is_container(p_value jsonb) returns boolean
language sql immutable
as $$ select coalesce(jsonb_typeof(p_value) in ('object', 'array'), false) $$;

-- -------------------------------------------------------------------------------------
-- Structural JSON diff: RFC 6901 paths to changed leaves.
-- JSON null and a missing key are the same thing; a container against a scalar is a
-- type change and is reported whole.
-- -------------------------------------------------------------------------------------
create or replace function audit.jsonb_delta(p_path text, p_old jsonb, p_new jsonb)
returns table (path text, old_value jsonb, new_value jsonb)
language plpgsql immutable
as $$
declare
    v_old   jsonb := nullif(p_old, 'null'::jsonb);
    v_new   jsonb := nullif(p_new, 'null'::jsonb);
    v_key   text;
    v_count integer;
begin
    if v_old is null and v_new is null then
        return;
    end if;

    if coalesce(jsonb_typeof(v_old), 'object') = 'object' and coalesce(jsonb_typeof(v_new), 'object') = 'object' then
        for v_key in
            select k from jsonb_object_keys(coalesce(v_old, '{}'::jsonb)) as o(k)
            union
            select k from jsonb_object_keys(coalesce(v_new, '{}'::jsonb)) as n(k)
        loop
            return query
                select * from audit.jsonb_delta(
                    p_path || '/' || replace(replace(v_key, '~', '~0'), '/', '~1'),
                    v_old -> v_key,
                    v_new -> v_key);
        end loop;
        return;
    end if;

    if coalesce(jsonb_typeof(v_old), 'array') = 'array' and coalesce(jsonb_typeof(v_new), 'array') = 'array' then
        v_count := greatest(coalesce(jsonb_array_length(v_old), 0), coalesce(jsonb_array_length(v_new), 0));
        for i in 0 .. v_count - 1 loop
            return query select * from audit.jsonb_delta(p_path || '/' || i, v_old -> i, v_new -> i);
        end loop;
        return;
    end if;

    if v_old is not distinct from v_new then
        return;
    end if;

    path := p_path;
    old_value := v_old;
    new_value := v_new;
    return next;
end
$$;

-- -------------------------------------------------------------------------------------
-- PII. Patterns and paths are split on '/'
-- exactly like string.Split('/'); '*' matches one segment, '**' any number.
-- -------------------------------------------------------------------------------------
create or replace function audit.path_matches(p_pattern text[], p_path text[]) returns boolean
language plpgsql immutable
as $$
begin
    if cardinality(p_pattern) = 0 then
        return true;
    end if;

    if p_pattern[1] = '**' then
        return audit.path_matches(p_pattern[2:], p_path)
            or (cardinality(p_path) > 0 and audit.path_matches(p_pattern, p_path[2:]));
    end if;

    return cardinality(p_path) > 0
        and (p_pattern[1] = '*' or p_pattern[1] = p_path[1])
        and audit.path_matches(p_pattern[2:], p_path[2:]);
end
$$;

-- p_patterns: JSON null => not personal; [] => the whole property; else JSON-pointer patterns.
create or replace function audit.is_personal(p_patterns jsonb, p_path text, p_container boolean) returns boolean
language sql immutable
as $$
    select case
        when p_patterns is null or jsonb_typeof(p_patterns) <> 'array' then false
        when jsonb_array_length(p_patterns) = 0 or p_container then true
        else exists (
            select 1
              from jsonb_array_elements_text(p_patterns) as t(pattern)
             where audit.path_matches(regexp_split_to_array(t.pattern, '/'), regexp_split_to_array(p_path, '/')))
    end
$$;

-- -------------------------------------------------------------------------------------
-- The row recorder: turns one row image pair into one audit.entries row.
-- SECURITY DEFINER: the application role needs no INSERT on audit.entries.
-- -------------------------------------------------------------------------------------
create or replace function audit.record(p_relation regclass, p_op text, p_old_row jsonb, p_new_row jsonb) returns void
language plpgsql
security definer
set search_path = pg_catalog, pg_temp
as $$
declare
    v_cfg       audit.tracked_entity;
    v_deleted   boolean := p_op = 'DELETE';
    v_column    jsonb;
    v_property  text;
    v_kind      text;
    v_patterns  jsonb;
    v_old       jsonb;
    v_new       jsonb;
    v_delta     record;
    v_changes   jsonb   := '{}'::jsonb;
    v_pii_paths jsonb   := '[]'::jsonb;
    v_from_app  boolean := audit.ctx('channel') is not null;
begin
    select * into v_cfg from audit.tracked_entity where relation = p_relation;
    if not found then
        return;  -- trigger left behind without config: do nothing rather than fail the write
    end if;

    for v_column in select value from jsonb_array_elements(v_cfg.columns) loop
        v_property := v_column ->> 'property';
        v_kind     := v_column ->> 'kind';
        v_patterns := v_column -> 'pii';
        v_old      := audit.normalize(p_old_row -> (v_column ->> 'column'), v_kind);
        v_new      := case when v_deleted then null else audit.normalize(p_new_row -> (v_column ->> 'column'), v_kind) end;

        if v_kind = 'json' and not v_deleted then
            -- An update of a jsonb column is diffed to its leaves; a delete snapshots it whole (below).
            for v_delta in select * from audit.jsonb_delta('', v_old, v_new) loop
                v_changes := v_changes || jsonb_build_object(
                    v_property || v_delta.path,
                    jsonb_build_object('old', v_delta.old_value, 'new', v_delta.new_value));

                if audit.is_personal(v_patterns, v_delta.path,
                                     audit.is_container(v_delta.old_value) or audit.is_container(v_delta.new_value)) then
                    v_pii_paths := v_pii_paths || to_jsonb(v_property || v_delta.path);
                end if;
            end loop;
        elsif v_deleted or v_old is distinct from v_new then
            v_changes := v_changes || jsonb_build_object(v_property, jsonb_build_object('old', v_old, 'new', v_new));

            if audit.is_personal(v_patterns, '', audit.is_container(v_old) or audit.is_container(v_new)) then
                v_pii_paths := v_pii_paths || to_jsonb(v_property);
            end if;
        end if;
    end loop;

    -- Only ignored columns changed (updated_at, a view counter): not an auditable update.
    -- A delete is always recorded, even of a key-only row.
    if not v_deleted and v_changes = '{}'::jsonb then
        return;
    end if;

    insert into audit.entries (
        occurred_at, company_id, operation, action, entity_type, entity_key,
        actor_kind, actor_id, actor_display, on_behalf_of_customer_id, channel,
        ip_address, user_agent, changes, pii_paths)
    values (
        now(),
        case when v_cfg.company_column is not null then (p_old_row ->> v_cfg.company_column)::integer end,
        coalesce(audit.ctx('operation'), 'unattributed'),
        case when v_deleted then 'Delete' else 'Update' end,
        v_cfg.entity_type,
        array_to_string(array(
            select p_old_row ->> k.name
              from unnest(v_cfg.key_columns) with ordinality as k(name, ordinal)
             order by k.ordinal), '|'),
        coalesce(audit.ctx('actor_kind'), 'System'),
        audit.ctx('actor_id')::integer,
        -- No application context: the best "who" left is the database role that ran the statement.
        left(case when v_from_app then audit.ctx('actor_display') else session_user::text end, 320),
        audit.ctx('on_behalf_of_customer_id')::integer,
        coalesce(audit.ctx('channel'), 'Database'),
        audit.ctx('ip_address'),
        left(audit.ctx('user_agent'), 512),
        v_changes,
        v_pii_paths);
end
$$;

-- -------------------------------------------------------------------------------------
-- The capture trigger. One generic function for every audited table.
--
-- BEFORE, and named zz_ so it runs last among BEFORE triggers (they fire in name order) and
-- sees the final NEW. If the statement later fails, the transaction rolls the audit row back
-- with it.
-- -------------------------------------------------------------------------------------
create or replace function audit.capture() returns trigger
language plpgsql
as $$
begin
    perform audit.record(
        tg_relid::regclass,
        tg_op,
        to_jsonb(old),
        case when tg_op = 'DELETE' then null else to_jsonb(new) end);

    if tg_op = 'DELETE' then
        return old;
    end if;

    return new;
end
$$;

-- -------------------------------------------------------------------------------------
-- Registration. Validates column names so a model/schema drift fails the deploy instead of
-- silently producing empty audit rows.
-- -------------------------------------------------------------------------------------
create or replace function audit.enable(
    p_relation       regclass,
    p_entity_type    text,
    p_key_columns    text[],
    p_company_column text,
    p_columns        jsonb)
returns void
language plpgsql
as $$
declare
    v_missing  text[];
    v_bad_kind text[];
begin
    select array_agg(c.name) into v_missing
      from unnest(p_key_columns
                  || array[p_company_column]
                  || array(select e.item ->> 'column' from jsonb_array_elements(p_columns) as e(item))) as c(name)
     where c.name is not null
       and not exists (select 1
                         from pg_attribute a
                        where a.attrelid = p_relation and a.attname = c.name
                          and a.attnum > 0 and not a.attisdropped);
    if v_missing is not null then
        raise exception 'audit.enable(%): unknown column(s) %', p_relation, v_missing;
    end if;

    select array_agg(e.item ->> 'column') into v_bad_kind
      from jsonb_array_elements(p_columns) as e(item)
     where coalesce(e.item ->> 'kind', '') not in ('value', 'json', 'timestamptz', 'timestamp');
    if v_bad_kind is not null then
        raise exception 'audit.enable(%): unknown kind for column(s) %', p_relation, v_bad_kind;
    end if;

    insert into audit.tracked_entity as t (relation, entity_type, key_columns, company_column, columns)
    values (p_relation, p_entity_type, p_key_columns, p_company_column, p_columns)
    on conflict (relation) do update set
        entity_type    = excluded.entity_type,
        key_columns    = excluded.key_columns,
        company_column = excluded.company_column,
        columns        = excluded.columns;

    -- Updates and deletes only: an insert has no "before" worth recording.
    execute format(
        'create or replace trigger zz_audit_capture before update or delete on %s '
        'for each row execute function audit.capture()', p_relation);
end
$$;

create or replace function audit.disable(p_relation regclass) returns void
language plpgsql
as $$
begin
    execute format('drop trigger if exists zz_audit_capture on %s', p_relation);
    delete from audit.tracked_entity where relation = p_relation;
end
$$;

-- -------------------------------------------------------------------------------------
-- "Every table is audited" guard: an integration test asserts this lists exactly the
-- tables whose entities opt out with [AuditIgnore].
-- -------------------------------------------------------------------------------------
create or replace view audit.untracked_tables as
select c.oid::regclass as relation
  from pg_class c
  join pg_namespace n on n.oid = c.relnamespace
 where c.relkind in ('r', 'p')
   and not c.relispartition
   and n.nspname not in ('audit', 'warp', 'information_schema')
   and n.nspname not like 'pg\_%'
   and c.relname <> '__EFMigrationsHistory'
   and not exists (select 1 from audit.tracked_entity t where t.relation = c.oid);
