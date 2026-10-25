# Mecesoft.Data

Mecesoft.Data, yeniden kullanılabilir veri erişim bileşenleri için .NET 10 tabanlı bir paket ailesidir. Bu repository şu anda solution foundation aşamasındadır; CRUD, Unit of Work, Specification, tenant filtreleri ve provider adaptörleri henüz public API olarak yayımlanmamıştır.

## Projeler

- `Mecesoft.Data.Abstractions`: EF Core ve SQL provider bağımlılığı olmayan sözleşme paketi.
- `Mecesoft.Data.EntityFrameworkCore`: EF Core 10 Relational tabanlı entegrasyon paketi.
- `Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy`: Opsiyonel SQL Server production tenant collation doğrulama paketi.
- `Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy`: Opsiyonel PostgreSQL production tenant collation doğrulama paketi.
- `Mecesoft.Data.Tests`: Abstractions mimari sınır ve smoke testleri.
- `Mecesoft.Data.EntityFrameworkCore.Tests`: EF Core bağımlılık sınır ve smoke testleri.

## Hedefler

- Target framework: `net10.0`
- Başlangıç paket sürümü: `1.0.0`
- Lisans: MIT
- Nullable reference types, implicit usings, .NET analyzers ve XML documentation aktiftir.
- Paket sürümleri Central Package Management ile yönetilir.

## Mimari sınırlar

`Mecesoft.Data.Abstractions` EF Core'a bağımlı değildir. `Mecesoft.Data.EntityFrameworkCore` yalnızca `Microsoft.EntityFrameworkCore.Relational` ve abstractions projesine bağlıdır; SQL Server, PostgreSQL, MySQL/MariaDB, SQLite veya Oracle provider paketlerinden herhangi birini doğrudan taşımaz.

Eski `Mecesoft.Repository` kaynakları davranış envanteriyle birlikte `legacy/` altında, aktif solution dışında korunur. Bu kaynaklar yeni paketlerin derlemesine dahil edilmez.

## Güvenlik sınırı

EF Core write güvenliği, `DbContext.SaveChanges` üzerinden izlenen değişiklikler için tasarlanmıştır. Tenant filtresi, soft-delete filtresi, tenant sahipliği, korunan alanlar ve concurrency token kontrolleri bu yol üzerinde uygulanır. Bu kontroller bir veritabanı güvenlik sınırı değildir.

Aşağıdaki kullanımlar `SaveChanges` doğrulamasını atlar ve uygulama tarafından ayrıca güvenli hale getirilmelidir:

- `ExecuteSqlRaw` / `ExecuteSqlInterpolated` ve benzeri raw SQL komutları.
- `ExecuteUpdate` / `ExecuteDelete` gibi change tracker'ı ve `SaveChanges` doğrulamasını atlayan toplu komutlar.
- Doğrudan `DbConnection` veya harici bir bağlantı üzerinden yapılan yazmalar.
- Parameterless `IgnoreQueryFilters()` veya `IgnoreQueryFilters([MecesoftQueryFilterKeys.Tenant])` ile tenant filtresinin kaldırılması; soft-delete filtresi yalnızca kontrollü soft-delete/restore repository kapsamındaki `IgnoreSoftDelete` ile kaldırılabilir.
- Veritabanı tarafındaki trigger, prosedür, job veya başka bir uygulama üzerinden yapılan değişiklikler.
- `MecesoftDbContext` dışında oluşturulan bir EF Core context'i üzerinden yapılan yazmalar.

Doğrudan `MecesoftDbContext.SaveChanges` üzerinden tenant entity yazmak için resolver tarafından onaylanmış bir write scope gerekir. `TenantScopeStore.TrySet` tek başına yazma yetkisi vermez; write authorization yalnızca `TenantScopeCoordinator` üzerinden resolver onayıyla oluşturulur ve bir sonraki `SaveChanges` tarafından tüketilir. Doğrudan `IgnoreSoftDelete` çağrısı da yalnızca kontrollü soft-delete/restore repository kapsamında çalışır; yetkisiz deleted-read `TenantConfigurationException` ile reddedilir. Raw SQL, bulk komutlar ve doğrudan `IgnoreQueryFilters` yolları yine ayrı bypass sınırlarıdır.

Doğrudan `DbSet`/`IQueryable` okumaları da resolver tarafından onaylanmış, context instance'ına bağlı bir read grant gerektirir. `TenantScopeStore.TrySet(victim, Read)` veya `Clear` tek başına okuma yetkisi vermez; grant yoksa `TenantId` query filter değerlendirmesi `TenantConfigurationException` ile fail-closed olur. Repository okuma yolları grant'i coordinator üzerinden kendisi kurar; doğrudan okuma için desteklenen yol `TenantScopeCoordinator.ExecuteAsync(context, TenantOperation.Read, ...)` overload'ıdır. Read grant yalnızca `Read` operation'ı ile verilebilir, context instance'ına bağlıdır ve `ExecuteAsync` veya repository okuma işlemi tamamlandığında temizlenir; dönen deferred query veya async enumerable grant temizlendikten sonra fail-closed olur. Materialize edilen entity'lerin tenant kimliği ayrıca resolved read scope ile karşılaştırılır.

Write grant'i genel okuma yetkisi vermez. Repository'lerin tenant filtresi altında yükleme yapması için kullanılan internal tenant resolution public read grant'ten ayrıdır; write tamamlandıktan sonra aynı context üzerinden doğrudan okuma yapılamaz. Read/write grant'leri context ve operation'a bağlıdır; UnitOfWork commit, rollback ve dispose işlemleri grant'leri temizler. `TenantScopeCoordinator.ResolveAsync(context, operation, ...)` write grant'ini context'e bağlar; eski store-only `ResolveAsync` çağrısı ilk `SaveChanges` tarafından tek context'e claim edilir.

`UseModel` ve compiled model ile dışarıdan verilen EF modelleri reddedilir; tenant ve soft-delete filtrelerinin context model pipeline'ı tarafından kurulmuş olması zorunludur.

Raw SQL yalnızca tenant kimliği parametre olarak bağlanarak, çağıran tarafından yetkilendirilmiş bir tenant kapsamı içinde ve mümkünse repository/specification katmanı üzerinden kullanılmalıdır. Tenant kimliğinin string birleştirme ile sorguya eklenmesi yasaktır.

### Veritabanı satır sahipliği

`SaveChanges`, `Modified` ve `Deleted` tenant entity'leri için veritabanındaki satırın tenant kimliğini `GetDatabaseValues` ile ayrıca doğrular. Bu, doğrudan `Attach` edilerek oluşturulan sahte entity'lerin başka bir tenant'ın satırını güncellemesini/silmesini engeller. Bu doğrulama her değişen tenant entity'si için ek bir `SELECT` üretir; toplu yazmalarda maliyet artabilir. `GetDatabaseValues` desteklenmiyorsa veya satır bulunamıyorsa davranış fail-closed'dır. Bu yine de bir veritabanı güvenlik sınırı değildir; raw SQL, bulk komutlar ve harici erişim bu kontrolü atlar.

### Global entity politikası

Paylaşılan lookup tabloları açıkça global olarak işaretlenmelidir:

```csharp
using Mecesoft.Data.EntityFrameworkCore.Modeling;

modelBuilder.Entity<Country>(entity =>
{
    entity.IsGlobal();
});
```

Global entity:

- `ITenantEntity<>` implement edemez ve tenant mapping'e sahip olamaz.
- Tenant-owned bir entity'ye FK ile bağlanamaz.
- Tenant entity'leri tarafından principal olarak referans edilebilir.
- Yazma işlemleri varsayılan olarak **deny**'dir. `TrySet` global write yetkisi vermez.

Global write yalnızca explicit, güvenilir bir `IGlobalWriteAuthorizer` ve `GlobalWriteCoordinator` üzerinden yapılabilir:

```csharp
using Mecesoft.Data.EntityFrameworkCore.Authorization;

services.AddMecesoftGlobalWriteAuthorization<GlobalWriteAuthorizer>();

var coordinator = scope.ServiceProvider.GetRequiredService<GlobalWriteCoordinator>();
DataResult<bool> authorization = await coordinator.AuthorizeAsync(
    context,
    country,
    TenantOperation.Create);
if (authorization.IsFailure)
{
    // fail-closed
}

context.Countries.Add(country);
```

Grant context instance'ına, root global entity referansına ve operation'a bağlıdır; sonraki `SaveChanges` tarafından tüketilir. Global-root owned entity yazmaları da aynı root grant'ini gerektirir. Global read işlemleri tenant read grant'i gerektirmez ve değişmez. Mixed global + tenant graph'larda tenant-owned ve global taraflar kendi grant'lerini ayrı ayrı sağlamalıdır.
- Tenant filtresine girmez; tüm tenant'lar tarafından okunabilir.

Açıkça global işaretlenmeyen ve bir tenant entity'sinden erişilebilen ilişkili tipler fail-closed olarak reddedilir.

### Cascade delete politikası

Soft-deletable bir child, non-soft-deletable bir principal'dan `Cascade` veya `ClientCascade` ile silinemez; model oluşturma `TenantConfigurationException` ile reddedilir. Bu tür ilişkilerde `DeleteBehavior.Restrict` kullanılmalıdır. Aksi halde principal'ın fiziksel silinmesi, child'ın merkezi `SaveChanges` doğrulamasından geçmeden fiziksel olarak silinmesine yol açabilir.

Global bir principal'dan tenant-owned dependent'a `Cascade` veya `ClientCascade` da model oluşturma aşamasında reddedilir. Global entity silindiğinde dependent satırların DB cascade ile silinmemesi için `Restrict` veya `NoAction` kullanılmalıdır.

Owned entity ilişkileri ayrıca doğrulanır. Owned entity'nin owner dışındaki tenant-owned veya global principal ilişkileri model ve SaveChanges aşamasında kontrol edilir; cross-tenant FK fail-closed reddedilir. Owned entity'den tenant-owned veya global principal'a `Cascade`/`ClientCascade` kullanılamaz; `Restrict`/`NoAction` kullanılmalıdır. Ownership FK'nin normal owned lifecycle cascade davranışı korunur.

Tenant-owned bir root owner'a bağlı owned entity'lerde `Added`/`Modified`/`Deleted` işlemleri resolver tarafından verilmiş sırasıyla `Create`/`Update`/`HardDelete` write grant'i gerektirir. Owner `Unchanged` iken yapılan doğrudan owned delete bağımsız `HardDelete` yetkisi ister; yetkili owner delete sırasında EF'nin ownership cascade ile `Deleted` yaptığı owned entry'ler owner'ın `HardDelete` grant'i altında değerlendirilir. Root owner bulunamaz, tenant scope ile eşleşmez veya karma operation graph oluşursa `SaveChanges` fail-closed olur. Global root owner'lar tenant scope taşımadığı için tenant write kontrolünün dışındadır; bunlar yukarıdaki explicit global write policy'sine tabidir.

Owned entity tipleri `ISoftDeletable` implement edemez. Owned soft-delete tam olarak desteklenmediği için model doğrulaması bu kombinasyonu fail-closed reddeder; soft-delete ownership root'a veya ayrı bir tenant-owned entity'ye uygulanmalıdır.

### Transaction ve participant sınırı

Commit participant'ları commit sırasında `CommitAsync`, `RollbackAsync`, `BeginTransactionAsync` veya `DisposeAsync` çağıramaz. Tenant scope commit boyunca dondurulur; participant'ın store değişikliği commit sonunda geri alınır. Commit, rollback veya dispose hatasında `Indeterminate` state korunur ve incelenebilir kalır. Bu kurallar in-process kod için bir güvenlik sınırı değildir; raw SQL ve doğrudan context erişimi ayrıca ele alınmalıdır.

Tüm repository'ler key selector'ın doğrudan primary key olduğunu doğrular; nested veya yanlış selector'lar `ArgumentException` ile reddedilir. Çoklu `DbContext` kaydında unkeyed `IUnitOfWork` ambiguous olduğu için fail-closed olarak başarısız olur; repository'ler kendi context'lerine ait keyed unit-of-work kullanır.

### String tenant ID ve collation

Guid tenant ID'ler için ek yapılandırma gerekmez. String tenant ID'ler varsayılan olarak **fail-closed**'dur: model oluşturma, context üzerinde bir `ITenantCollationVerifier` tanımlanmamışsa `TenantConfigurationException` ile durur. Verifier, gerçek veritabanı/kolon collation'ını runtime'da doğrulamalıdır; EF model metadata'sı tek başına güvenlik garantisi değildir. Case-sensitive veya binary olmayan bir collation doğrulanırsa okuma/yazma `TenantConfigurationFailure` ile reddedilir.

String tenant ID'ler **canonical** olmalıdır: boş olamaz, en fazla `TenantIdValidation.MaxStringLength` (128) UTF-16 code unit olabilir ve başında/sonunda Unicode whitespace bulunamaz. Doğrulama ordinal ve kültürden bağımsızdır; değer otomatik olarak trim/normalize edilmez. Bu kural `TenantScopeStore`, tenant resolver koordinasyonu, entity tenant değerleri, doğrudan `SaveChanges` ve repository tarafından materialize edilen kayıtlar için uygulanır. Guid tenant ID'ler bu string kurallarına tabi değildir.

Model oluşturma aşamasında `TenantPropertyName` gerçek EF property'si olmalı, `TenantIdType` gerçek CLR tipiyle uyuşmalı ve tenant property üzerinde value converter veya computed column bulunmamalıdır. Entity'nin view'a map edilmesi reddedilir. Tenant property, entity'nin `ITenantEntity<T>.TenantId` contract property'si olmalıdır; başka bir property'yi tenant kolonu olarak gösteren custom mapping fail-closed reddedilir. Tenant property'nin tam olarak tek bir tablo kolonuna map edildiği kontrolü model finalize edildikten sonra, ilk güvenlik doğrulamasında veya startup'ta yapılır.

Tenant query filter'ının kendisi framework tarafından, doğrulanmış `TenantPropertyName` metadata'sından üretilir. Custom `ITenantEntityMapping.BuildFilter` çıktısı isolation sınırı olarak kullanılmaz; yalnızca doğrudan tenant property eşitliği ve opsiyonel null guard içerdiği doğrulanır. `entity => true`, başka bir property kullanan veya ek predicate ekleyen custom filter'lar `TenantConfigurationException` ile model oluşturma aşamasında reddedilir. Custom mapping metadata'sı context oluşturulurken snapshot alınır; model oluşturulduktan sonra mapping üzerinde yapılan mutation write-validation veya verifier property'sini değiştiremez. `ITenantExactMapping` marker'ı tek başına güvenlik kanıtı değildir; property ve filter doğrulaması ayrıca yapılır.

Tenant ve soft-delete mapping setleri EF Core model cache anahtarına dahil edilir. Aynı context tipi ve options ile farklı bir mapping seti kullanılırsa önceki model yeniden kullanılmaz; mapping metadata'sı context oluşturulurken ayrıca CLR contract doğrulamasından geçirilir. Global entity annotation set'i de model cache anahtarına dahil edilir; aynı context tipiyle farklı global mapping yapılandırmaları önceki modeli paylaşamaz. `SaveChanges` ve `ValidateTenantSecurity`, cache hit durumunda da mevcut mapping'leri model metadata'sıyla yeniden doğrular. Custom soft-delete visibility predicate'leri yapısal olarak fingerprint'lenir; closure veya static mutable state'e bağlı predicate'ler fail-closed reddedilir. `MecesoftDbContext.OnConfiguring` sealed'dır; model cache service registration derived context tarafından değiştirilemez.

Uygulama başlangıcında tenant scope gerektirmeyen açık doğrulama:

```csharp
using var scope = services.CreateScope();
var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
context.ValidateTenantSecurity();
```

`ValidateTenantSecurity()` model/mapping/collation doğrulamasını çalıştırır; mevcut satırlardaki tenant verisini taramaz ve raw SQL/bulk/`IgnoreQueryFilters` bypass'larını kaldırmaz. SQL Server exactness extension'ı kullanıldığında legacy trailing-space satırları EF sorgularından filtrelenir; raw SQL ve doğrudan DB erişimi yine ayrı bir sınırdır. Canonical olmayan veriler otomatik düzeltilmez.

SQL Server'da `nvarchar` `=` karşılaştırması trailing space'leri yok sayabildiği için canonical write/read validation ve exactness extension'ı birlikte kullanılmalıdır. PostgreSQL `text` trailing space'e duyarlıdır; iki provider arasındaki fark nedeniyle tenant ID'ler her iki tarafta da canonical tutulmalıdır.

SQL Server için testlerdeki referans verifier `tests/Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests` projesindedir; test amaçlıdır ve `_CS_`/`_BIN` gibi daha geniş bir kümeyi kabul eder. Production paketi yalnızca `nvarchar` + `_BIN2` kabul eder; bütün `varchar` kombinasyonları reddedilir.

SQL Server production doğrulaması için opsiyonel `Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy` paketi kullanılabilir. Bu paket yalnızca user table, `nvarchar`, `_BIN2` collation ve encrypted/computed olmayan kolonları kabul eder; custom schema, quoted identifier ve `HasColumnName` metadata'sını destekler. Model explicit bir schema içermiyorsa verifier, SQL Server'ın gerçek unqualified çözümlemesini `OBJECT_ID`/schema metadata'sı üzerinden bulur; hardcoded `dbo` fallback'i güvenlik kanıtı olarak kullanılmaz. Aynı tablo adı farklı şemalarda bulunuyorsa yalnızca EF'nin gerçekten eriştiği şema doğrulanır. `varchar`, `_BIN2_UTF8`, `char`, `nchar`, `text`, `ntext` ve alias/user-defined type reddedilir. `sys.columns.max_length` doğrulanır: `nvarchar` en az 256 byte veya `MAX` olmalıdır; daha küçük kolonlar fail-closed reddedilir. Production verifier ve exactness tek API ile etkinleştirilir:

```csharp
optionsBuilder
    .UseSqlServer(connectionString)
    .UseMecesoftSqlServerTenancy();
```

Bu API production collation verifier’ı ve exact tenant filter augmenter’ını birlikte kurar. String mapping varken yapılandırma eksikse startup/model validation fail-closed olur. Production verifier yalnızca built-in `SqlServerTenantFilterExpressionAugmenter` örneğini kabul eder; no-op veya custom augmenter SQL Server exactness olarak kabul edilmez. `LEN(TenantId + 'x') = LEN(@tenant + 'x')` predicate'i tüm string tenant mapping'lerine uygulanır; `nvarchar` için parametre tipinden bağımsızdır ve composite index üzerinde Index Seek + Filter kullanır. Custom string mapping'ler `ITenantExactMapping` marker'ını taşımıyorsa model oluşturma fail-closed olur. Guid tenant ID'ler bu kısıttan etkilenmez; raw SQL, `IgnoreQueryFilters` ve bulk yolları yine bypass'dır.

Core'daki additive `ITenantFilterExpressionAugmenter` extension point'i provider-specific exactness'in named tenant filter'a eklenmesini sağlar; `UseMecesoftTenantSecurity` ile verifier ve augmenter birlikte options'a bağlanır. SQL Server paketi bunu `UseMecesoftSqlServerTenancy` üzerinden uygular.

PostgreSQL production doğrulaması için opsiyonel `Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy` paketi kullanılabilir:

```csharp
optionsBuilder
    .UseNpgsql(connectionString)
    .UseMecesoftPostgreSqlTenancy();
```

Bu API trusted production verifier'ı kurar; string tenant mapping varken yapılandırma eksikse `ValidateTenantSecurity()` fail-closed olur. Verifier gerçek `pg_attribute`/`pg_class`/`pg_collation` metadata'sını okur. Model explicit schema içermiyorsa `search_path` üzerinden gerçek görünür şema (`pg_table_is_visible`) çözülür; hardcoded `public` fallback'i güvenlik kanıtı olarak kullanılmaz. Şema güvenilir biçimde çözülemezse doğrulama fail-closed olur. Yalnızca regular/partitioned table üzerinde, generated olmayan `text`/`varchar` ve `pg_collation.collisdeterministic = true` kabul edilir; `citext`, `bpchar`, domain, view, foreign table ve non-deterministic ICU collation reddedilir. `pg_attribute.atttypmod` doğrulanır: `text`, limitsiz `varchar` veya en az 128 karakterlik `varchar(n)` kabul edilir; daha küçük kolonlar fail-closed reddedilir. Custom schema, quoted identifier ve `HasColumnName` desteklenir. Custom string mapping'ler `ITenantExactMapping` marker'ını taşımıyorsa fail-closed olur. PostgreSQL deterministic collation eşitliği byte-wise olduğu için SQL Server'daki trailing-space exactness augmenter'ı PostgreSQL'e uygulanmaz; `RequireExactTenantFilters` bu pakette etkin değildir. Guid tenant ID'ler bu kısıttan etkilenmez.

SQL Server ve PostgreSQL production verifier'ları model foreign key delete action'larını gerçek catalog metadata'sıyla karşılaştırır. Schema-qualified tablo/kolon çözümlemesi kullanılır; EF modelinde bulunmayan veya delete action'ı modelle uyuşmayan bir `ON DELETE CASCADE`/`SET NULL`/`SET DEFAULT` fail-closed reddedilir. Özellikle global principal'dan tenant-owned dependent'a giden bir database cascade kabul edilmez. Eksik veya belirsiz catalog metadata'sı da doğrulamayı durdurur. Bu kontrol veritabanını değiştirmez ve mevcut legacy satırları taramaz.

SQL Server'da `Snapshot` isolation için `BeginTransactionAsync` başarılı olsa bile veritabanında `ALLOW_SNAPSHOT_ISOLATION` açık değilse ilk veri erişimi fail eder. Snapshot'a güvenmeden önce veritabanı ayarı doğrulanmalıdır.

PostgreSQL için referans verifier `tests/Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests` projesindedir; `pg_attribute`/`pg_collation` üzerinden etkin kolon tipini ve deterministic collation'ı kontrol eder. Yalnızca `text`/`varchar` ve deterministic collation kabul edilir; `citext`, `bpchar` ve non-deterministic ICU collation'lar reddedilir.

PostgreSQL'de `xmin` sistem kolonu Npgsql tarafından `uint` rowversion/concurrency token olarak eşlenir. Mevcut adapter `uint` token tipini desteklemediği için bu yapılandırma **fail-closed** olarak reddedilir. PostgreSQL'de uygulama-yönetimli `Guid` (veya desteklenen `byte[]`/`long`/`int`/`string`) concurrency token kullanılmalıdır; `xmin` desteği ayrı bir adapter/public API kararı gerektirir.

PostgreSQL'de `Snapshot` isolation Npgsql tarafından `REPEATABLE READ` olarak gönderilir; PostgreSQL'in `REPEATABLE READ` seviyesi snapshot semantiği sağlar. `DateTimeOffset` yalnızca `Offset=0` ile yazılabilir; `timestamp with time zone` UTC saklar, non-UTC offset provider tarafından reddedilir.

SQL Server ve PostgreSQL entegrasyon testleri varsayılan olarak skip edilir ve yalnızca ayrı test projelerinde provider bağımlılığı taşır. Etkinleştirmek için `MECESOFT_SQLSERVER_TEST_CONNECTION` veya `MECESOFT_POSTGRES_TEST_CONNECTION` ortam değişkeni tanımlanmalıdır. PostgreSQL fixture'ı yalnızca `mecesoft_data_pg_` önekli benzersiz geçici veritabanları oluşturur ve bunları `DROP DATABASE ... WITH (FORCE)` ile temizler.

## Soft delete ve restore

`EfSoftDeleteRepository` yalnızca aktif bir UnitOfWork transaction'ı ve çözülmüş bir tenant kapsamı içinde çalışır. Soft delete aktif kaydı, restore silinmiş kaydı tenant filtresi altında yükler; restore yalnızca adlandırılmış soft-delete filtresini kaldırır, tenant filtresini kaldırmaz.

Her iki işlem de `ISoftDeleteAuthorizer` ile resource seviyesinde yetkilendirilir. Yetkilendirme başarılı olsa bile tenant sahipliği, concurrency token ve protected alanlar yeniden doğrulanır. Soft-delete durum geçişi yalnızca repository'nin oluşturduğu içsel bir grant ile `SaveChanges` tarafından kabul edilir; doğrudan `SaveChanges` üzerinden `IsDeleted`/`DeletedAtUtc` değiştirmek veya `ISoftDeletable` bir kaydı fiziksel olarak silmek reddedilir.

```csharp
services.AddMecesoftSoftDeleteRepository<AppDbContext, Order, Guid, Guid>(
    order => order.Id);
services.AddScoped<ISoftDeleteAuthorizer<Order, Guid, Guid>, OrderSoftDeleteAuthorizer>();
```

Repository scoped olarak çözülür. Authorizer kaydı yoksa çözümleme fail-closed olarak başarısız olur.

## Yerel doğrulama

```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

Provider entegrasyonları, transaction davranışı ve tenant güvenliği sonraki fazlarda gerçek veritabanlarıyla ayrıca doğrulanacaktır.
