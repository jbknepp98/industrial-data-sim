#!/bin/sh
# flow_data_sim bootstrap for the postgres image's first start (empty volume).
# The image runs this as POSTGRES_USER against POSTGRES_DB (flow_data_sim).
set -eu
: "${FLOWSIM_WRITER_PASSWORD:?Set FLOWSIM_WRITER_PASSWORD in postgres.env}"
: "${FLOWSIM_READER_PASSWORD:?Set FLOWSIM_READER_PASSWORD in postgres.env}"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v writer_password="$FLOWSIM_WRITER_PASSWORD" -v reader_password="$FLOWSIM_READER_PASSWORD" \
  -f /flowsim/01_bootstrap.sql
