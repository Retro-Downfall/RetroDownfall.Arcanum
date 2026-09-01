using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Adapts Entity Framework connection callbacks to the process-wide ordinary Grimoire lifecycle.
/// </summary>
internal sealed class CovenantConnectionEnrolmentInterceptor : DbConnectionInterceptor
{

    private readonly IGrimoireOrdinaryConnectionLifecycle _lifecycle;

    private readonly ICovenantConnectionDrain _drain;

    private readonly ICovenantSqliteConnectionInitializer _initializer;

    private readonly Lock _gate = new();

    private readonly ConditionalWeakTable<DbConnection, InterceptorRegistration> _registrations = [];

    internal CovenantConnectionEnrolmentInterceptor(
        IGrimoireOrdinaryConnectionLifecycle lifecycle,
        ICovenantConnectionDrain drain,
        ICovenantSqliteConnectionInitializer initializer)
    {

        ArgumentNullException.ThrowIfNull(lifecycle);

        ArgumentNullException.ThrowIfNull(drain);

        ArgumentNullException.ThrowIfNull(initializer);

        _lifecycle = lifecycle;

        _drain = drain;

        _initializer = initializer;

    }

    public override InterceptionResult ConnectionOpening(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {

        BeginOpen(connection);

        return base.ConnectionOpening(connection, eventData, result);

    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {

        BeginOpen(connection);

        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);

    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {

        InterceptorRegistration registration = RequireRegistration(connection);

        Result revalidated = registration.Registration.RevalidateAfterNativeOpen();

        registration.NativeOpenRevalidated = true;

        if (revalidated.IsFailure)
        {

            RefuseAfterPhysicalClose(connection);

            throw new GrimoireMaintenanceUnavailableException();

        }

        try
        {

            if (connection is SqliteConnection sqlite)
            {

                _initializer.InitializeAsync(
                        sqlite,
                        CovenantSqliteConnectionMode.ReadWrite,
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

            }

            Result opened = registration.Registration.MarkOpened();

            if (opened.IsFailure)
            {

                RefuseAfterPhysicalClose(connection);

                throw new GrimoireMaintenanceUnavailableException();

            }

            registration.Opened = true;

        }
        catch
        {

            if (!registration.Opened && HasRegistration(connection))
            {

                RefuseAfterPhysicalClose(connection);

            }

            throw;

        }

        base.ConnectionOpened(connection, eventData);

    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {

        InterceptorRegistration registration = RequireRegistration(connection);

        Result revalidated = registration.Registration.RevalidateAfterNativeOpen();

        registration.NativeOpenRevalidated = true;

        if (revalidated.IsFailure)
        {

            await RefuseAfterPhysicalCloseAsync(connection).ConfigureAwait(false);

            throw new GrimoireMaintenanceUnavailableException();

        }

        try
        {

            if (connection is SqliteConnection sqlite)
            {

                await _initializer.InitializeAsync(
                        sqlite,
                        CovenantSqliteConnectionMode.ReadWrite,
                        cancellationToken)
                    .ConfigureAwait(false);

            }

            Result opened = registration.Registration.MarkOpened();

            if (opened.IsFailure)
            {

                await RefuseAfterPhysicalCloseAsync(connection).ConfigureAwait(false);

                throw new GrimoireMaintenanceUnavailableException();

            }

            registration.Opened = true;

        }
        catch
        {

            if (!registration.Opened && HasRegistration(connection))
            {

                await RefuseAfterPhysicalCloseAsync(connection).ConfigureAwait(false);

            }

            throw;

        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken)
            .ConfigureAwait(false);

    }

    public override void ConnectionFailed(
        DbConnection connection,
        ConnectionErrorEventData eventData)
    {

        Release(connection, closePhysicalConnection: true);

        base.ConnectionFailed(connection, eventData);

    }

    public override async Task ConnectionFailedAsync(
        DbConnection connection,
        ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {

        await ReleaseAsync(connection, closePhysicalConnection: true).ConfigureAwait(false);

        await base.ConnectionFailedAsync(connection, eventData, cancellationToken)
            .ConfigureAwait(false);

    }

    public override void ConnectionCanceled(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {

        Release(connection, closePhysicalConnection: true);

        base.ConnectionCanceled(connection, eventData);

    }

    public override async Task ConnectionCanceledAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {

        await ReleaseAsync(connection, closePhysicalConnection: true).ConfigureAwait(false);

        await base.ConnectionCanceledAsync(connection, eventData, cancellationToken)
            .ConfigureAwait(false);

    }

    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData)
    {

        Release(connection, closePhysicalConnection: false);

        base.ConnectionClosed(connection, eventData);

    }

    public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {

        Release(connection, closePhysicalConnection: false);

        return base.ConnectionClosedAsync(connection, eventData);

    }

    public override void ConnectionDisposed(DbConnection connection, ConnectionEndEventData eventData)
    {

        Release(connection, closePhysicalConnection: false);

        base.ConnectionDisposed(connection, eventData);

    }

    public override Task ConnectionDisposedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {

        Release(connection, closePhysicalConnection: false);

        return base.ConnectionDisposedAsync(connection, eventData);

    }

    private void BeginOpen(DbConnection connection)
    {

        lock (_gate)
        {

            if (_registrations.TryGetValue(connection, out _))
            {

                throw new InvalidOperationException(
                    "This physical Grimoire connection already has an interceptor registration.");

            }

            _registrations.Add(
                connection,
                new InterceptorRegistration(_lifecycle.BeginOpen(connection)));

            connection.StateChange += OnPhysicalStateChanged;

        }

    }

    private void OnPhysicalStateChanged(object? sender, StateChangeEventArgs change)
    {

        if (change.CurrentState == ConnectionState.Closed && sender is DbConnection connection)
        {

            _lifecycle.ReleaseAfterExternalClose(connection);

            Release(connection, closePhysicalConnection: false);

        }

    }

    private InterceptorRegistration RequireRegistration(DbConnection connection)
    {

        lock (_gate)
        {

            return _registrations.TryGetValue(connection, out InterceptorRegistration? registration)
                ? registration
                : throw new InvalidOperationException(
                    "This physical Grimoire connection has no interceptor registration.");

        }

    }

    private bool HasRegistration(DbConnection connection)
    {

        lock (_gate)
        {

            return _registrations.TryGetValue(connection, out _);

        }

    }

    private void RefuseAfterPhysicalClose(DbConnection connection)
    {

        InterceptorRegistration? registration = TryBeginRelease(connection);

        if (registration is null)
        {

            return;

        }

        try
        {

            if (!IsPhysicallyClosed(connection))
            {

                connection.Close();

            }

            ClearExactPoolAfterClose(connection);

            registration.Registration.MarkRefusedAfterOpen();

            registration.Registration.Dispose();

            CompleteRelease(connection, registration);

        }
        catch
        {

            CancelRelease(connection, registration);

            throw;

        }

    }

    private async Task RefuseAfterPhysicalCloseAsync(DbConnection connection)
    {

        InterceptorRegistration? registration = TryBeginRelease(connection);

        if (registration is null)
        {

            return;

        }

        try
        {

            if (!IsPhysicallyClosed(connection))
            {

                await connection.CloseAsync().ConfigureAwait(false);

            }

            ClearExactPoolAfterClose(connection);

            registration.Registration.MarkRefusedAfterOpen();

            registration.Registration.Dispose();

            CompleteRelease(connection, registration);

        }
        catch
        {

            CancelRelease(connection, registration);

            throw;

        }

    }

    private void Release(DbConnection connection, bool closePhysicalConnection)
    {

        InterceptorRegistration? registration = TryBeginRelease(connection);

        if (registration is null)
        {

            return;

        }

        try
        {

            RevalidateObservedNativeOpen(connection, registration);

            if (closePhysicalConnection && !IsPhysicallyClosed(connection))
            {

                connection.Close();

            }

            if (!registration.Opened && registration.NativeOpenRevalidated)
            {

                ClearExactPoolAfterClose(connection);

            }

            CompleteRegistration(registration);

            CompleteRelease(connection, registration);

        }
        catch
        {

            CancelRelease(connection, registration);

            throw;

        }

    }

    private async Task ReleaseAsync(DbConnection connection, bool closePhysicalConnection)
    {

        InterceptorRegistration? registration = TryBeginRelease(connection);

        if (registration is null)
        {

            return;

        }

        try
        {

            RevalidateObservedNativeOpen(connection, registration);

            if (closePhysicalConnection && !IsPhysicallyClosed(connection))
            {

                await connection.CloseAsync().ConfigureAwait(false);

            }

            if (!registration.Opened && registration.NativeOpenRevalidated)
            {

                ClearExactPoolAfterClose(connection);

            }

            CompleteRegistration(registration);

            CompleteRelease(connection, registration);

        }
        catch
        {

            CancelRelease(connection, registration);

            throw;

        }

    }

    private static void CompleteRegistration(InterceptorRegistration registration)
    {

        if (!registration.Opened)
        {

            if (registration.NativeOpenRevalidated)
            {

                registration.Registration.MarkRefusedAfterOpen();

            }
            else
            {

                registration.Registration.MarkFailed();

            }

        }

        registration.Registration.Dispose();

    }

    private static void RevalidateObservedNativeOpen(
        DbConnection connection,
        InterceptorRegistration registration)
    {

        if (registration.Opened
            || registration.NativeOpenRevalidated
            || connection.State != ConnectionState.Open)
        {

            return;

        }

        _ = registration.Registration.RevalidateAfterNativeOpen();

        registration.NativeOpenRevalidated = true;

    }

    private InterceptorRegistration? TryBeginRelease(DbConnection connection)
    {

        lock (_gate)
        {

            if (!_registrations.TryGetValue(connection, out InterceptorRegistration? registration)
                || registration.ReleaseInProgress)
            {

                return null;

            }

            registration.ReleaseInProgress = true;

            return registration;

        }

    }

    private void CompleteRelease(
        DbConnection connection,
        InterceptorRegistration registration)
    {

        lock (_gate)
        {

            if (_registrations.TryGetValue(connection, out InterceptorRegistration? current)
                && ReferenceEquals(current, registration))
            {

                connection.StateChange -= OnPhysicalStateChanged;

                _ = _registrations.Remove(connection);

            }

        }

    }

    private void CancelRelease(
        DbConnection connection,
        InterceptorRegistration registration)
    {

        lock (_gate)
        {

            if (_registrations.TryGetValue(connection, out InterceptorRegistration? current)
                && ReferenceEquals(current, registration))
            {

                registration.ReleaseInProgress = false;

            }

        }

    }

    private static bool IsPhysicallyClosed(DbConnection connection)
    {

        try
        {

            return connection.State == ConnectionState.Closed;

        }
        catch (ObjectDisposedException)
        {

            return true;

        }

    }

    private void ClearExactPoolAfterClose(DbConnection connection)
    {

        if (connection is not SqliteConnection sqlite)
        {

            return;

        }

        Result cleared = _drain.ClearExactPoolAfterClose(sqlite);

        if (cleared.IsFailure)
        {

            throw new InvalidOperationException(cleared.Error.Message);

        }

        if (!IsPhysicallyClosed(connection))
        {

            throw new InvalidOperationException(
                "A refused ordinary Grimoire open remained physically open.");

        }

    }

    private sealed class InterceptorRegistration(
        IGrimoireOrdinaryConnectionRegistration registration)
    {

        internal IGrimoireOrdinaryConnectionRegistration Registration { get; } = registration;

        internal bool NativeOpenRevalidated { get; set; }

        internal bool Opened { get; set; }

        internal bool ReleaseInProgress { get; set; }

    }

}
