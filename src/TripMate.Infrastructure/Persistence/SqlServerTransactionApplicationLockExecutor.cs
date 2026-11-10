using System.Data;
using System.Diagnostics;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using TripMate.Application.Common.Locks;

namespace TripMate.Infrastructure.Persistence;

internal sealed class SqlServerTransactionApplicationLockExecutor(ApplicationDbContext dbContext)
{
    private const string SharedMode = "Shared";
    private const string ExclusiveMode = "Exclusive";
    private readonly Dictionary<string, string> _acquiredModes = new(StringComparer.Ordinal);
    private Guid? _transactionId;

    public async Task AcquireAsync(
        IReadOnlyList<string> resources,
        string lockMode,
        string resourceCategory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceCategory);
        if (lockMode is not SharedMode and not ExclusiveMode)
        {
            throw new ArgumentOutOfRangeException(nameof(lockMode));
        }

        var currentTransaction = dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                $"The {resourceCategory} lock requires a caller-owned transaction.");

        if (_transactionId != currentTransaction.TransactionId)
        {
            _acquiredModes.Clear();
            _transactionId = currentTransaction.TransactionId;
        }

        if (dbContext.Database.GetDbConnection() is not SqlConnection connection
            || currentTransaction.GetDbTransaction() is not SqlTransaction transaction)
        {
            throw new InvalidOperationException("The transaction lock requires SQL Server.");
        }

        var budget = Stopwatch.StartNew();
        foreach (string resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_acquiredModes.TryGetValue(resource, out string? acquiredMode))
            {
                if (acquiredMode == SharedMode && lockMode == ExclusiveMode)
                {
                    throw new InvalidOperationException(
                        $"The {resourceCategory} lock cannot be upgraded within a transaction.");
                }

                continue;
            }

            var remainingMilliseconds = RemainingBudgetMilliseconds(budget.ElapsedMilliseconds);
            if (remainingMilliseconds <= 0)
            {
                throw new TransactionLockAcquisitionException(
                    TransactionLockFailureKind.LockTimeout,
                    resourceCategory);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = SqlServerApplicationLockContract.CommandTimeoutSeconds;
            command.CommandType = CommandType.Text;
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = @lockMode,
                    @LockOwner = N'Transaction',
                    @LockTimeout = @lockTimeout,
                    @DbPrincipal = @dbPrincipal;
                SELECT @result;
                """;
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255)
            {
                Value = resource,
            });
            command.Parameters.Add(new SqlParameter("@lockMode", SqlDbType.NVarChar, 32)
            {
                Value = lockMode,
            });
            command.Parameters.Add(new SqlParameter("@lockTimeout", SqlDbType.Int)
            {
                Value = remainingMilliseconds,
            });
            command.Parameters.Add(new SqlParameter("@dbPrincipal", SqlDbType.NVarChar, 128)
            {
                Value = SqlServerApplicationLockContract.DatabasePrincipal,
            });

            try
            {
                var result = Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                EnsureAcquired(result, resourceCategory, cancellationToken);
                _acquiredModes.Add(resource, lockMode);
            }
            catch (SqlException exception) when (
                IsCallerCancellation(exception, cancellationToken))
            {
                throw new OperationCanceledException(
                    "The transaction lock acquisition was canceled.",
                    exception,
                    cancellationToken);
            }
            catch (SqlException exception)
            {
                throw new TransactionLockAcquisitionException(
                    ClassifySqlError(exception.Number),
                    resourceCategory,
                    exception);
            }
        }
    }

    internal static void EnsureAcquired(
        int result,
        string resourceCategory,
        CancellationToken cancellationToken)
    {
        if (result is 0 or 1)
        {
            return;
        }

        if (result > 1)
        {
            throw new TransactionLockAcquisitionException(
                TransactionLockFailureKind.LockProtocolError,
                resourceCategory);
        }

        if (result == -2 && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var failureKind = result switch
        {
            -1 => TransactionLockFailureKind.LockTimeout,
            -2 => TransactionLockFailureKind.LockCanceledByServer,
            -3 => TransactionLockFailureKind.DeadlockVictim,
            -999 => TransactionLockFailureKind.LockProtocolError,
            _ => TransactionLockFailureKind.ProviderFailure,
        };

        throw new TransactionLockAcquisitionException(failureKind, resourceCategory);
    }

    internal static TransactionLockFailureKind ClassifySqlError(int number) =>
        number switch
        {
            1205 => TransactionLockFailureKind.DeadlockVictim,
            -2 => TransactionLockFailureKind.CommandTimeout,
            _ => TransactionLockFailureKind.ProviderFailure,
        };

    private static bool IsCallerCancellation(
        SqlException exception,
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
        && exception.Errors
            .Cast<SqlError>()
            .Any(error =>
                error.Number == 0
                && error.Message.Contains(
                    "Operation cancelled by user.",
                    StringComparison.OrdinalIgnoreCase));

    internal static int RemainingBudgetMilliseconds(long elapsedMilliseconds) =>
        (int)Math.Max(
            0,
            SqlServerApplicationLockContract.TotalBudgetMilliseconds
                - Math.Min(int.MaxValue, elapsedMilliseconds));
}