# Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy

> Mecesoft.Data 1.1.0 ile standart `DbContext`, `IdentityDbContext` ve
> `Mecesoft.Repository` uyumluluk katmanı desteklenir. Bu paket yalnızca row-per-tenant
> production collation/relationship doğrulaması için gereklidir; tenant kullanmayan veya
> DB-per-tenant uygulamalar bu pakete ihtiyaç duymaz.

Opsiyonel PostgreSQL production tenant collation doğrulama paketi.

## Kapsam

Bu paket yalnızca PostgreSQL'e özgü `ITenantCollationVerifier` implementasyonunu sağlar. Ana
`Mecesoft.Data.EntityFrameworkCore` paketi provider-neutral kalır.

## Kullanım

```csharp
optionsBuilder
    .UseNpgsql(connectionString)
    .UseMecesoftPostgreSqlTenancy();
```

Tek API trusted production verifier'ı kurar. String tenant mapping varken bu yapılandırma
eksikse model/startup doğrulaması fail-closed olur.

## Doğrulama kuralları

- Tenant property gerçek EF table/column mapping metadata'sından çözülür. Tenant property
  `ITenantEntity<T>.TenantId` contract property'si olmalıdır.
- Custom schema, quoted identifier ve `HasColumnName` desteklenir.
- Explicit schema yoksa `search_path` üzerinden gerçek görünür şema çözülür; hardcoded `public`
  fallback'i güvenlik kanıtı olarak kullanılmaz. Şema çözülemez veya belirsizse fail-closed olur.
- Custom string mapping'ler `ITenantExactMapping` marker'ını taşımalıdır; taşımıyorsa fail-closed olur.
- Marker tek başına güvenlik kanıtı değildir: core, custom `BuildFilter`'ın doğrudan tenant
  property eşitliği içerdiğini ve mapping metadata'sının model oluşturulmadan önce snapshot
  alındığını doğrular. `entity => true`, başka property veya ek predicate kullanan filter'lar
  model oluşturma aşamasında reddedilir.
- Yalnızca regular veya partitioned table (`pg_class.relkind` `r`/`p`) kabul edilir.
- Yalnızca `text`/`varchar` kabul edilir.
- `pg_attribute.atttypmod` doğrulanır: `text`, limitsiz `varchar` veya en az 128 karakterlik
  `varchar(n)` kabul edilir; daha küçük kapasiteli kolonlar fail-closed reddedilir.
- `citext`, `bpchar`, domain ve diğer custom type'lar reddedilir.
- Generated/computed tenant column reddedilir.
- View, materialized view, foreign table reddedilir.
- Yalnızca `pg_collation.collisdeterministic = true` kabul edilir.
- Non-deterministic ICU collation reddedilir.
- Model foreign key delete action'ları `pg_constraint`/`pg_class`/`pg_namespace` ile
  schema-qualified olarak karşılaştırılır. Modelde bulunmayan veya modelle uyuşmayan
  `ON DELETE CASCADE`/`SET NULL`/`SET DEFAULT` fail-closed reddedilir. Eksik/ambiguous metadata
  doğrulamayı durdurur.
- Catalog sorgusu parametreledir; identifier interpolation kullanılmaz.

PostgreSQL deterministic collation'larda eşitlik byte-wise'dır ve trailing space'ler anlamlıdır;
SQL Server'daki exactness augmenter'ı PostgreSQL'e uygulanmaz.

## Startup

```csharp
using var scope = services.CreateScope();
var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
context.ValidateTenantSecurity();
```

String tenant kullanılıyorsa schema hazır olduktan sonra çağrılmalıdır.

## Kapsam dışı

- `xmin` concurrency token desteği değiştirilmez; mevcut fail-closed davranışı korunur.
- Raw SQL, bulk ve `IgnoreQueryFilters` named tenant filter'ı atlar.
- Paket mevcut veriyi taramaz veya normalize etmez.
