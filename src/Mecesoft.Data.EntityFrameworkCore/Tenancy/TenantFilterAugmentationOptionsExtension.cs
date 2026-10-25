using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

internal sealed class TenantFilterAugmentationOptionsExtension
    : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public TenantFilterAugmentationOptionsExtension(
        ITenantFilterExpressionAugmenter augmenter)
        : this(
            augmenter,
            verifier: null,
            requireTrustedVerifier: false,
            requireExactTenantFilters: false)
    {
    }

    public TenantFilterAugmentationOptionsExtension(
        ITenantFilterExpressionAugmenter? augmenter,
        ITenantCollationVerifier? verifier,
        bool requireTrustedVerifier,
        bool requireExactTenantFilters)
    {
        if (requireExactTenantFilters && augmenter is null)
        {
            throw new ArgumentNullException(nameof(augmenter));
        }

        Augmenter = augmenter;
        Verifier = verifier;
        RequireTrustedVerifier = requireTrustedVerifier;
        RequireExactTenantFilters = requireExactTenantFilters;
    }

    public ITenantFilterExpressionAugmenter? Augmenter { get; }

    public ITenantCollationVerifier? Verifier { get; }

    public bool RequireTrustedVerifier { get; }

    public bool RequireExactTenantFilters { get; }

    public DbContextOptionsExtensionInfo Info =>
        _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        private readonly TenantFilterAugmentationOptionsExtension _extension;

        public ExtensionInfo(TenantFilterAugmentationOptionsExtension extension)
            : base(extension)
        {
            _extension = extension;
        }

        public override bool IsDatabaseProvider => false;

        public override string LogFragment
            => "using Mecesoft tenant filter augmentation ";

        public override int GetServiceProviderHashCode()
        {
            var hash = new HashCode();
            hash.Add(_extension.Augmenter?.GetType());
            hash.Add(_extension.Verifier?.GetType());
            hash.Add(_extension.RequireTrustedVerifier);
            hash.Add(_extension.RequireExactTenantFilters);
            return hash.ToHashCode();
        }

        public override bool ShouldUseSameServiceProvider(
            DbContextOptionsExtensionInfo other)
        {
            return other is ExtensionInfo otherInfo
                && ReferenceEquals(
                    _extension.Augmenter,
                    otherInfo._extension.Augmenter)
                && ReferenceEquals(
                    _extension.Verifier,
                    otherInfo._extension.Verifier)
                && _extension.RequireTrustedVerifier
                    == otherInfo._extension.RequireTrustedVerifier
                && _extension.RequireExactTenantFilters
                    == otherInfo._extension.RequireExactTenantFilters;
        }

        public override void PopulateDebugInfo(
            IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);
            debugInfo["Mecesoft:TenantFilterAugmenter"] =
                _extension.Augmenter?.GetType().FullName ?? "none";
            debugInfo["Mecesoft:TenantCollationVerifier"] =
                _extension.Verifier?.GetType().FullName ?? "none";
            debugInfo["Mecesoft:RequireTrustedVerifier"] =
                _extension.RequireTrustedVerifier.ToString();
            debugInfo["Mecesoft:RequireExactTenantFilters"] =
                _extension.RequireExactTenantFilters.ToString();
        }
    }
}
