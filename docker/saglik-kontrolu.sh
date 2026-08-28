#!/bin/sh
# Worker sağlık kontrolü (P4-05).
#
# İKİ ŞEYE BİRDEN BAKIYOR ve ikisi de gerekli:
#
#   1. Dosya TAZE mi — süreç donduysa ya da öldüyse kalp atışı eskir.
#   2. `healthy` true mu — süreç ayakta ama bütün kuyruk döngüleri
#      aralıksız düşüyorsa false.
#
# Yalnızca tazeliğe baksaydık bugün yaşanan arızayı kaçırırdık: süreç
# canlıydı, dosya tazeydi, hiçbir video üretilmiyordu ve kap sağlıklı
# görünüyordu. Yalnızca `healthy`'ye baksaydık donmuş bir süreç
# sonsuza kadar "sağlıklı" kalırdı — çünkü dosyadaki son değer
# `true`'da donardı.

set -eu

DOSYA="${BMAI_HEARTBEAT:-/veri/durum/worker.json}"

# EŞİK, YAZMA SIKLIĞININ KATI OLMALI. Kalp atışı 10 saniyede bir
# yazılıyor; eşiği 10'a koymak, normal zamanlama sapmasında bile
# sağlıklı kapları öldürürdü.
ESIK="${BMAI_HEARTBEAT_MAX_AGE:-45}"

if [ ! -f "$DOSYA" ]; then
  echo "kalp atisi dosyasi yok: $DOSYA"
  exit 1
fi

SIMDI=$(date +%s)
YAZILDI=$(stat -c %Y "$DOSYA")
YAS=$((SIMDI - YAZILDI))

if [ "$YAS" -gt "$ESIK" ]; then
  echo "kalp atisi eski: ${YAS}sn (esik ${ESIK}sn)"
  exit 1
fi

# `grep` ile bakıyoruz çünkü kapta jq yok ve tek bir alan için bir
# paket daha kurmanın karşılığı yok.
if grep -q '"healthy":true' "$DOSYA"; then
  exit 0
fi

echo "dongulerden biri araliksiz dusuyor:"
cat "$DOSYA"
exit 1
