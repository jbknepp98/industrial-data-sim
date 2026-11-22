-- flow_data_sim bootstrap. Run once as the database owner (flowsim_admin):
--   psql -v writer_password=... -v reader_password=... -f 01_bootstrap.sql
-- Idempotent: safe to rerun; it never drops or alters a simulation's tables.

-- Roles: the simulator writes with flowsim_writer; Connect reads with flowsim_reader.
SELECT 'CREATE ROLE flowsim_writer LOGIN' WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'flowsim_writer') \gexec
SELECT 'CREATE ROLE flowsim_reader LOGIN' WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'flowsim_reader') \gexec
ALTER ROLE flowsim_writer PASSWORD :'writer_password';
ALTER ROLE flowsim_reader PASSWORD :'reader_password';
GRANT CONNECT ON DATABASE flow_data_sim TO flowsim_writer, flowsim_reader;
GRANT USAGE, CREATE ON SCHEMA public TO flowsim_writer;
GRANT USAGE ON SCHEMA public TO flowsim_reader;
-- Every table the writer creates is readable by Connect, and nothing else.
ALTER DEFAULT PRIVILEGES FOR ROLE flowsim_writer IN SCHEMA public GRANT SELECT ON TABLES TO flowsim_reader;

-- One change counter shared by every simulation table: row_version is the
-- connector's watermark (strictly increasing across inserts and updates).
CREATE SEQUENCE IF NOT EXISTS flowsim_row_version AS bigint;
ALTER SEQUENCE flowsim_row_version OWNER TO flowsim_writer;
CREATE OR REPLACE FUNCTION flowsim_stamp_row_version() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW.row_version := nextval('flowsim_row_version');
  RETURN NEW;
END $$;
ALTER FUNCTION flowsim_stamp_row_version() OWNER TO flowsim_writer;

-- Registry: which simulation owns which table prefix, its admitted model and
-- anchor (so every restart replays the identical history), and delivery progress.
CREATE TABLE IF NOT EXISTS flowsim_simulations (
  simulation        text PRIMARY KEY,
  table_prefix      text NOT NULL UNIQUE,
  model_json        text NOT NULL,
  model_sha256      text NOT NULL,
  anchor_utc        timestamptz NOT NULL,
  start_utc         timestamptz NOT NULL,
  created_utc       timestamptz NOT NULL DEFAULT now(),
  -- Last record change applied (engine sequence); rows are applied once, in order.
  applied_sequence  bigint NOT NULL DEFAULT 0,
  -- Historian: points are published through tag_cursor_utc. sending_until_utc is
  -- set before a publish and cleared after; a value found on restart means that
  -- batch was interrupted. Plant tags are deterministic, so it is sent again and
  -- counted in uncertain_batches. (flowsim_snapshots, the engine snapshots, is
  -- created by the simulator on first use.)
  tag_cursor_utc    timestamptz,
  sending_until_utc timestamptz,
  uncertain_batches bigint NOT NULL DEFAULT 0
);
ALTER TABLE flowsim_simulations OWNER TO flowsim_writer;
