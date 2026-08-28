#!/bin/bash
# Replikasyon bağlantılarına izin ver (P4-06).
#
# NEDEN AYRI BİR SATIR GEREKİYOR: resmi postgres imajı `pg_hba.conf`'a
# `host all all all <auth>` satırını ekliyor ama `replication` ÖZEL bir
# veritabanı adı ve `all` onu kapsamıyor. Bu satır olmadan replika
# şunu görüyor:
#
#   FATAL: no pg_hba.conf entry for replication connection
#
# Yalnızca Docker ağından: `scram-sha-256` parola istiyor ve kap
# 127.0.0.1'e bağlı, yani bu ağın dışından erişilemiyor.

set -eu

echo "host replication all all scram-sha-256" >> "$PGDATA/pg_hba.conf"

echo "replikasyon icin pg_hba satiri eklendi"
