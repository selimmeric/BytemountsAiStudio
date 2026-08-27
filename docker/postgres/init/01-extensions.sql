-- Yalnızca veritabanı ilk kez oluşturulurken çalışır (docker-entrypoint-initdb.d).
-- Şema bu dosyada tanımlanmaz — şema EF Core migration'larının işidir (P0-03).
-- Burada sadece uygulamanın varsaydığı eklentiler açılır.

-- Konu tekilliği embedding benzerliğiyle çözülüyor (ADR-003, mimari §20.5).
-- pgvector olmadan "En Tehlikeli 10 Yer" ile "En Tehlikeli 10 Bölge" ayırt edilemez.
CREATE EXTENSION IF NOT EXISTS vector;

-- Metin benzerliği: konu başlıklarında yazım farklarını yakalamak için
-- embedding'e ek ucuz bir ön filtre.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Kuyruk ve run kayıtlarında UUID üretimi.
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- Kurulumun doğrulanabilir bir izi kalsın.
DO $$
BEGIN
    RAISE NOTICE 'BytemountsAiStudio: eklentiler hazir (vector, pg_trgm, uuid-ossp).';
END
$$;
