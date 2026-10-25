# Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy

> Mecesoft.Data 1.1.0 ile standart `DbContext`, `IdentityDbContext` ve
> `Mecesoft.Repository` uyumluluk katmanı desteklenir. Bu paket yalnızca row-per-tenant
> production collation/relationship doğrulaması için gereklidir; tenant kullanmayan veya
> DB-per-tenant uygulamalar bu pakete ihtiyaç duymaz.

Opsiyonel SQL Server production tenant collation doğrulama paketi.

## Kapsam

Bu paket yalnızca SQL Server'a özgü `ITenantCollationVerifier` implementasyonunu sağlar. Ana
`Mecesoft.Data.EntityFrameworkCore` paketi provider-neutral kalır ve bu pakete bağımlı değildir.

## Kullanım

```csharp
protected override ITenantCollationVerifier? TenantCollationVerifier
    => SqlServerTenantCollationVerifier.Instance;
```

İsteğe bağlı DI kaydı:

```csharp
services.AddMecesoftSqlServerTenantCollationVerification();
```

DI kaydı context'i otomatik olarak değiştirmez; context, verifier'ı
`TenantCollationVerifier` üzerinden açıkça kullanmalıdır.

## Doğrulama kuralları

- Tenant property gerçek EF table/column mapping metadata'sından çözülür.
- Custom schema, quoted identifier ve `HasColumnName` desteklenir.
- Explicit schema yoksa SQL Server'ın gerçek unqualified çözümlemesi `OBJECT_ID`/schema
  metadata'sı üzerinden bulunur; hardcoded `dbo` fallback'i güvenlik kanıtı olarak kullanılmaz.
  Aynı tablo adı farklı şemalarda bulunuyorsa yalnızca EF'nin gerçekten eriştiği şema doğrulanır.
- Yalnızca kullanıcı tablosu (`sys.objects.type = 'U'`) kabul edilir.
- Yalnızca `nvarchar` kabul edilir. `varchar`, `char`, `nchar`, `text`, `ntext`,
  alias/user-defined type reddedilir.
- Kayıpsız Unicode için yalnızca `nvarchar` + `_BIN2` kabul edilir. Bütün `varchar`
  kombinasyonları, `_BIN2_UTF8` dahil, reddedilir.
- `sys.columns.max_length` doğrulanır: `nvarchar` en az 256 byte veya `MAX` olmalıdır;
  daha küçük kolonlar fail-closed reddedilir.
- `_CS_AS`, `_CS_AI`, `_CI_*`, `_BIN2_UTF8` ve legacy `_BIN` reddedilir.
- Computed ve encrypted kolonlar reddedilir.
- View, synonym ve diğer non-table object türleri reddedilir.
- Model foreign key delete action'ları `sys.foreign_keys`/`sys.foreign_key_columns` ile
  schema-qualified olarak karşılaştırılır. Modelde bulunmayan veya modelle uyuşmayan
  `ON DELETE CASCADE`/`SET NULL`/`SET DEFAULT` fail-closed reddedilir. Eksik/ambiguous metadata
  doğrulamayı durdurur.
- Catalog sorgusu parametreledir; identifier string interpolation kullanılmaz.

## Trailing-space exactness

SQL Server `nvarchar` `=` karşılaştırmaları trailing space'leri yok sayar. `_BIN2`
bunu değiştirmez. Paket, tenant query filter'ına şu exactness predicate'ini ekler:

```sql
TenantId = @tenant
AND LEN(TenantId + N'x') = LEN(@tenant + N'x')
```

Suffix, trailing space'leri internal hale getirir; böylece `LEN` onları sayar. Bu yöntem
`nvarchar` için parametre byte uzunluğuna bağımlı değildir. Equality predicate'i
korunduğu için SQL Server composite index üzerinde **Index Seek + Filter** kullanılabilir.

Exactness ve production verifier birlikte, tek API ile etkinleştirilmelidir:

```csharp
optionsBuilder
    .UseSqlServer(connectionString)
    .UseMecesoftSqlServerTenancy();
```

`UseMecesoftSqlServerTenancy()` production collation verifier’ı ve exact tenant filter augmenter’ını birlikte kurar. String tenant mapping varken bu yapılandırma eksikse model/startup doğrulaması fail-closed olur. Production verifier yalnızca built-in `SqlServerTenantFilterExpressionAugmenter` örneğini SQL Server exactness olarak kabul eder; no-op veya custom augmenter reddedilir. Eski `UseMecesoftSqlServerExactTenancy()` adı geriye uyumluluk için aynı güvenli yapılandırmaya yönlendirilir.

Bu extension tüm string tenant mapping'lerine uygulanır. String mapping'ler
`ITenantExactMapping` marker'ını taşımalıdır; `TenantEntityMapping<TEntity, TTenantId>` bunu
sağlar. Marker tek başına güvenlik kanıtı değildir: core, tenant property'nin `ITenantEntity<T>.TenantId`
contract property'si olduğunu ve custom `BuildFilter`'ın doğrudan tenant property eşitliği
içerdiğini ayrıca doğrular. Custom mapping marker taşımıyorsa veya filter'ı genişletmeye
çalışıyorsa model oluşturma **fail-closed** olur. Guid tenant mapping'leri değişmez.

Exactness extension'ı olmadan SQL Server string tenant desteği **Limited** kalır. Raw SQL,
`IgnoreQueryFilters`, bulk ve doğrudan DB değişiklikleri exactness'i atlar.

## Startup

```csharp
using var scope = services.CreateScope();
var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
context.ValidateTenantSecurity();
```

String tenant kullanılıyorsa schema hazır olduktan sonra çağrılmalıdır.
