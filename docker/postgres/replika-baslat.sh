#!/bin/bash
# Okuma replikası başlatıcı (P4-06).
#
# İLK AÇILIŞTA `pg_basebackup`, SONRAKİLERDE DOĞRUDAN BAŞLAT.
#
# Veri dizini doluyken yeniden kopyalamak, her yeniden başlatmada
# bütün veritabanını ağdan çekmek demekti — ve replikanın birikmiş
# durumunu atmak.
#
# `pg_basebackup -R` standby yapılandırmasını (`standby.signal` ve
# `primary_conninfo`) kendisi yazıyor; elle yazmak PostgreSQL sürümüne
# bağlı bir dosya biçimini tekrar etmek olurdu.

set -eu

VERI="${PGDATA:-/var/lib/postgresql/data}"

if [ ! -s "$VERI/PG_VERSION" ]; then
  echo "replika: veri dizini bos, birincilden kopyalaniyor..."

  # BİRİNCİL HAZIR OLANA KADAR BEKLE. Compose `service_healthy`
  # bekliyor ama kap sağlıklı olduktan sonra bile ilk saniyelerde
  # bağlantı reddedilebiliyor; burada beklemek, kabın hata verip
  # yeniden başlama döngüsüne girmesinden iyi.
  for i in $(seq 1 60); do
    if pg_isready -h "$BIRINCIL_HOST" -U "$PGUSER" >/dev/null 2>&1; then
      break
    fi
    sleep 2
  done

  rm -rf "${VERI:?}"/*

  # `-R`: standby yapılandırmasını yaz.
  # `-X stream`: kopyalama sırasında WAL'ı da akıt — yoksa uzun süren
  #              bir kopyalamada birincil gerekli WAL'ı geri
  #              dönüştürebilir ve replika hiç başlayamaz.
  # `-S`: kalıcı yuva; replika kapalıyken birincil onun ihtiyacı olan
  #       WAL'ı SAKLIYOR. Yuvasız bir replika birkaç saat kapalı
  #       kalınca kalıcı olarak geri kalıyor.
  pg_basebackup \
    -h "$BIRINCIL_HOST" \
    -U "$PGUSER" \
    -D "$VERI" \
    -R -X stream -S "$YUVA" -C \
    --progress --verbose

  chmod 0700 "$VERI"

  echo "replika: kopyalama bitti"
fi

exec postgres
