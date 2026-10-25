# PostgreSQL entegrasyon testleri

Bu proje yalnızca PostgreSQL 17 conformance testlerini içerir. Ana paketlere provider
bağımlılığı eklemez; `Npgsql.EntityFrameworkCore.PostgreSQL` sadece bu test projesinde
referans edilir.

## Etkinleştirme

Testler varsayılan olarak skip edilir. Yalnızca aşağıdaki ortam değişkeni tanımlıysa
çalışır:

```bash
export MECESOFT_POSTGRES_TEST_CONNECTION="Host=127.0.0.1;Port=14335;Username=postgres;Password=<test-password>;Database=postgres"
dotnet test tests/Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests/Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests.csproj -c Release
```

Bağlantıdaki rol `CREATEDB` yetkisine sahip olmalıdır. Fixture'lar yalnızca
`mecesoft_data_pg_<guid>` önekli benzersiz geçici veritabanları oluşturur ve test sonunda
`DROP DATABASE ... WITH (FORCE)` ile siler. Mevcut veritabanlarına dokunulmaz.

## Test container planı (ayrı onay gerektirir)

| Öğe | Değer |
|---|---|
| Image | `postgres:17` (yerelde yok; indirme onayı gerekir) |
| Container | `mecesoft-postgres-5c3` |
| Port | `127.0.0.1:14335` |
| Volume | Yok (ephemeral writable layer) |
| Database | `postgres` (yalnızca yönetim bağlantısı) |
| Healthcheck | `pg_isready -U postgres` |

Container başlatıldıktan sonra yukarıdaki ortam değişkeniyle testler çalıştırılır.
Temizlik ayrı onayla yapılır: önce `mecesoft_data_pg_%` kalıntısı kontrol edilir, sonra
container durdurulup silinir ve geçici şifre dosyası kaldırılır.

## Kapsam

- Guid tenant read/add/update izolasyonu
- Uygulama-yönetimli `Guid` concurrency token ve gerçek eşzamanlı çakışma
- Soft delete/restore, tenant ve concurrency doğrulaması
- Cross-tenant FK reddi ve fabricated attach koruması
- Commit/rollback ve participant tenant freeze
- DateTimeOffset UTC roundtrip; non-UTC offset reddi
- ProviderDefault/ReadUncommitted/ReadCommitted/RepeatableRead/Serializable/Snapshot
- Snapshot'ın PostgreSQL `REPEATABLE READ` snapshot semantiği
- Unique violation → `PersistenceFailure`
- String tenant pozitif/negatif collation testleri (deterministic vs non-deterministic ICU)
- `xmin` fail-closed testleri

## Bilinen sınırlar

- `xmin` Npgsql tarafından `uint` concurrency token olarak eşlenir; mevcut adapter `uint`
  token tipini desteklemez ve repository/SaveChanges fail-closed olur. Uygulama-yönetimli
  `Guid` token kullanılmalıdır.
- `DateTimeOffset` yalnızca `Offset=0` ile yazılabilir; PostgreSQL `timestamptz` UTC saklar.
- `DataIsolationLevel.Snapshot` Npgsql tarafından `REPEATABLE READ` olarak gönderilir;
  PostgreSQL'in `REPEATABLE READ` seviyesi snapshot semantiği sağlar.
- Provider-specific FK/deadlock hataları typed failure'a çevrilmez; unique violation
  generic `PersistenceFailure` döner.
