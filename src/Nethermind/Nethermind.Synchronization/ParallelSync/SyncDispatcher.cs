//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    public abstract class SyncDispatcher<T>
    {
        private readonly object _feedStateManipulation = new();
        private SyncFeedState _currentFeedState = SyncFeedState.Dormant;

        private IPeerAllocationStrategyFactory<T> PeerAllocationStrategyFactory { get; }

        protected ILogger Logger { get; }
        protected ISyncFeed<T> Feed { get; }
        protected ISyncPeerPool SyncPeerPool { get; }

        protected SyncDispatcher(
            ISyncFeed<T>? syncFeed,
            ISyncPeerPool? syncPeerPool,
            IPeerAllocationStrategyFactory<T>? peerAllocationStrategy,
            ILogManager? logManager)
        {
            Logger = logManager?.GetClassLogger<SyncDispatcher<T>>() ?? throw new ArgumentNullException(nameof(logManager));
            Feed = syncFeed ?? throw new ArgumentNullException(nameof(syncFeed));
            SyncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            PeerAllocationStrategyFactory = peerAllocationStrategy ?? throw new ArgumentNullException(nameof(peerAllocationStrategy));

            syncFeed.StateChanged += SyncFeedOnStateChanged;
        }

        private TaskCompletionSource<object?>? _dormantStateTask = new();

        protected abstract Task Dispatch(PeerInfo peerInfo, T request, CancellationToken cancellationToken);

        public async Task Start(CancellationToken cancellationToken)
        {
            UpdateState(Feed.CurrentState);
            while (true)
            {
                try
                {
                    SyncFeedState currentStateLocal;
                    TaskCompletionSource<object?>? dormantTaskLocal;
                    lock (_feedStateManipulation)
                    {
                        currentStateLocal = _currentFeedState;
                        dormantTaskLocal = _dormantStateTask;
                    }

                    if (currentStateLocal == SyncFeedState.Dormant)
                    {
                        if (Logger.IsDebug) Logger.Debug($"{GetType().Name} is going to sleep.");
                        if (dormantTaskLocal == null)
                        {
                            if (Logger.IsWarn) Logger.Warn("Dormant task is NULL when trying to await it");
                        }

                        await (dormantTaskLocal?.Task ?? Task.CompletedTask).WaitAsync(cancellationToken);
                        if (Logger.IsDebug) Logger.Debug($"{GetType().Name} got activated.");
                    }
                    else if (currentStateLocal == SyncFeedState.Active)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        T request = await (Feed.PrepareRequest(cancellationToken) ?? Task.FromResult<T>(default!)); // just to avoid null refs
                        if (request == null)
                        {
                            if (!Feed.IsMultiFeed)
                            {
                                if (Logger.IsTrace) Logger.Trace($"{Feed.GetType().Name} enqueued a null request.");
                            }

                            await Task.Delay(10, cancellationToken);
                            continue;
                        }

                        SyncPeerAllocation allocation = await Allocate(request);
                        PeerInfo? allocatedPeer = allocation.Current;
                        if (Logger.IsTrace) Logger.Trace($"Allocated peer: {allocatedPeer}");
                        if (allocatedPeer != null)
                        {
                            if (Logger.IsTrace) Logger.Trace($"SyncDispatcher request: {request}, AllocatedPeer {allocation.Current}");
                            Task task = Dispatch(allocatedPeer, request, cancellationToken)
                                .ContinueWith(t =>
                            {
                                if (t.IsFaulted)
                                {
                                    if (Logger.IsWarn) Logger.Warn($"Failure when executing request {t.Exception}");
                                }

                                try
                                {
                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        if (Logger.IsDebug) Logger.Debug("Ignoring sync response as shutdown is requested.");
                                        return;
                                    }

                                    SyncResponseHandlingResult result = Feed.HandleResponse(request, allocatedPeer);
                                    ReactToHandlingResult(request, result, allocatedPeer);
                                }
                                catch (ObjectDisposedException)
                                {
                                    if (Logger.IsInfo) Logger.Info("Ignoring sync response as the DB has already closed.");
                                }
                                catch (Exception e)
                                {
                                    // possibly clear the response and handle empty response batch here (to avoid missing parts)
                                    // this practically corrupts sync
                                    if (Logger.IsError) Logger.Error("Error when handling response", e);
                                }
                                finally
                                {
                                    Free(allocation);
                                }
                            }, cancellationToken);

                            if (!Feed.IsMultiFeed)
                            {
                                if (Logger.IsDebug) Logger.Debug($"Awaiting single dispatch from {Feed.GetType().Name} with allocated {allocatedPeer}");
                                await task;
                                if (Logger.IsDebug) Logger.Debug($"Single dispatch from {Feed.GetType().Name} with allocated {allocatedPeer} has been processed");
                            }
                        }
                        else
                        {
                            Logger.Debug($"DISPATCHER - {this.GetType().Name}: peer NOT allocated");
                            SyncResponseHandlingResult result = Feed.HandleResponse(request);
                            ReactToHandlingResult(request, result, null);
                        }
                    }
                    else if (currentStateLocal == SyncFeedState.Finished)
                    {
                        if (Logger.IsInfo) Logger.Info($"{GetType().Name} has finished work.");
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    Feed.Finish();
                }
            }
        }

        protected virtual void Free(SyncPeerAllocation allocation)
        {
            SyncPeerPool.Free(allocation);
        }

        protected virtual async Task<SyncPeerAllocation> Allocate(T request)
        {
            SyncPeerAllocation allocation = await SyncPeerPool.Allocate(PeerAllocationStrategyFactory.Create(request), Feed.Contexts, 1000);
            return allocation;
        }

        private void ReactToHandlingResult(T request, SyncResponseHandlingResult result, PeerInfo? peer)
        {
            if (peer != null)
            {
                switch (result)
                {
                    case SyncResponseHandlingResult.Emptish:
                        break;
                    case SyncResponseHandlingResult.Ignored:
                        Logger.Error($"Feed response was ignored.");
                        break;
                    case SyncResponseHandlingResult.LesserQuality:
                        SyncPeerPool.ReportWeakPeer(peer, Feed.Contexts);
                        break;
                    case SyncResponseHandlingResult.NoProgress:
                        SyncPeerPool.ReportNoSyncProgress(peer, Feed.Contexts);
                        break;
                    case SyncResponseHandlingResult.NotAssigned:
                        break;
                    case SyncResponseHandlingResult.InternalError:
                        Logger.Error($"Feed {Feed} has reported an internal error when handling {request}");
                        break;
                    case SyncResponseHandlingResult.OK:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(result), result, null);
                }
            }
        }

        private void SyncFeedOnStateChanged(object? sender, SyncFeedStateEventArgs e)
        {
            SyncFeedState state = e.NewState;
            UpdateState(state);
        }

        private void UpdateState(SyncFeedState state)
        {
            lock (_feedStateManipulation)
            {
                if (_currentFeedState != state)
                {
                    if(Logger.IsDebug) Logger.Debug($"{Feed.GetType().Name} state changed to {state}");

                    _currentFeedState = state;
                    TaskCompletionSource<object?>? newDormantStateTask = null;
                    if (state == SyncFeedState.Dormant)
                    {
                        newDormantStateTask = new TaskCompletionSource<object?>();
                    }

                    var previous = Interlocked.Exchange(ref _dormantStateTask, newDormantStateTask);
                    previous?.TrySetResult(null);
                }
            }
        }
    }
}
