using Mecesoft.Data.Abstractions.Transactions;

namespace Mecesoft.Data.Abstractions.Results;

/// <summary>
/// Represents a provider-independent reason for a failed data operation.
/// </summary>
/// <param name="Code">A stable, machine-readable failure code.</param>
/// <param name="Message">A non-sensitive description suitable for diagnostics.</param>
public abstract record DataFailure(string Code, string Message);

/// <summary>
/// Represents a lookup that did not find the requested entity.
/// </summary>
/// <typeparam name="TKey">The entity key type.</typeparam>
/// <param name="EntityName">The logical entity name.</param>
/// <param name="Key">The requested entity key.</param>
public sealed record NotFoundFailure<TKey>(string EntityName, TKey Key)
    : DataFailure("data.not_found", $"A {EntityName} entity was not found.")
    where TKey : notnull;

/// <summary>
/// Represents an optimistic concurrency conflict.
/// </summary>
/// <typeparam name="TKey">The entity key type.</typeparam>
/// <param name="EntityName">The logical entity name.</param>
/// <param name="Key">The entity key.</param>
/// <param name="ExpectedToken">The concurrency token supplied by the caller.</param>
/// <param name="CurrentToken">The current token when it can be disclosed safely.</param>
public sealed record ConcurrencyFailure<TKey>(
    string EntityName,
    TKey Key,
    ConcurrencyToken ExpectedToken,
    ConcurrencyToken? CurrentToken = null)
    : DataFailure("data.concurrency_conflict", $"A concurrent change was detected for {EntityName}.")
    where TKey : notnull;

/// <summary>
/// Represents a required tenant scope that could not be resolved.
/// </summary>
/// <param name="Reason">A non-sensitive reason that does not disclose tenant identifiers.</param>
public sealed record TenantUnavailableFailure(string Reason)
    : DataFailure("data.tenant_required", Reason);

/// <summary>
/// Represents a denied data operation.
/// </summary>
/// <param name="Operation">The logical operation that was denied.</param>
/// <param name="Reason">A non-sensitive denial reason.</param>
public sealed record AuthorizationFailure(string Operation, string Reason)
    : DataFailure("data.access_denied", Reason);

/// <summary>
/// Represents invalid data-operation input.
/// </summary>
/// <param name="Reason">The validation failure description.</param>
public sealed record ValidationFailure(string Reason)
    : DataFailure("data.validation_failed", Reason);

/// <summary>
/// Represents missing or invalid tenant configuration.
/// </summary>
/// <param name="Reason">A non-sensitive description that does not disclose tenant identifiers.</param>
public sealed record TenantConfigurationFailure(string Reason)
    : DataFailure("data.tenant_configuration_invalid", Reason);

/// <summary>
/// Represents an entity graph that contains an entity outside the resolved tenant scope.
/// </summary>
/// <param name="Reason">A non-sensitive description that does not disclose tenant identifiers.</param>
public sealed record TenantOwnershipFailure(string Reason)
    : DataFailure("data.tenant_ownership_violation", Reason);

/// <summary>
/// Represents a mutation that changed a protected entity invariant.
/// </summary>
/// <param name="Invariant">The stable invariant name, such as <c>tenant_id</c> or <c>primary_key</c>.</param>
/// <param name="Reason">A non-sensitive description that does not disclose protected values.</param>
public sealed record MutationInvariantFailure(string Invariant, string Reason)
    : DataFailure("data.mutation_invariant_violation", Reason);

/// <summary>
/// Represents a write operation attempted without an active unit-of-work transaction.
/// </summary>
/// <param name="Operation">The logical operation that requires a transaction.</param>
public sealed record TransactionRequiredFailure(string Operation)
    : DataFailure("data.transaction_required", $"An active transaction is required for {Operation}.");

/// <summary>
/// Represents an invalid transaction state transition.
/// </summary>
/// <param name="State">The state that made the requested operation invalid.</param>
/// <param name="Reason">A non-sensitive description of the invalid transition.</param>
public sealed record TransactionStateFailure(DataTransactionState State, string Reason)
    : DataFailure("data.transaction_state_invalid", Reason);

/// <summary>
/// Represents a commit or rollback whose outcome is unknown.
/// </summary>
/// <param name="Reason">A non-sensitive description. The caller must not retry blindly.</param>
public sealed record IndeterminateCommitFailure(string Reason)
    : DataFailure("data.transaction_indeterminate", Reason);

/// <summary>
/// Represents a query without a stable ordering suitable for pagination.
/// </summary>
/// <param name="Reason">A non-sensitive description of the missing or invalid ordering.</param>
public sealed record UnstableOrderingFailure(string Reason)
    : DataFailure("data.unstable_ordering", Reason);

/// <summary>
/// Represents an expression or projection that the selected adapter cannot translate safely.
/// </summary>
/// <param name="ExpressionKind">The logical expression kind, such as <c>criteria</c>, <c>sort</c>, or <c>projection</c>.</param>
/// <param name="Reason">A non-sensitive description. The adapter must not execute the expression client-side.</param>
public sealed record UnsupportedExpressionFailure(string ExpressionKind, string Reason)
    : DataFailure("data.unsupported_expression", Reason);
