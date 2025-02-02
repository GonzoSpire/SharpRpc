﻿// Copyright © 2021 Soft-Fx. All rights reserved.
// Author: Andrei Hilevich
//
// This Source Code Form is subject to the terms of the Mozilla
// Public License, v. 2.0. If a copy of the MPL was not distributed
// with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using SharpRpc.Lib;
using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SharpRpc
{
    public class TxPipeline_NoQueue : TxPipeline
    {
        private readonly object _lockObj = new object();
        private readonly TxBuffer _buffer;
        private bool _isProcessingItem;
        private bool _isClosing;
        private bool _isStarted;
        private bool _isRequestedConnection;
        private bool _isUserMessagesEnabled;
        private RpcResult _fault;
        private readonly int _bufferSizeThreshold;
        private readonly TxAsyncGate _asyncGate = new TxAsyncGate();
        //private readonly Queue<IPendingItem> _asyncQueue = new Queue<IPendingItem>();
        //private readonly Queue<IPendingItem> _systemQueue = new Queue<IPendingItem>();
        private readonly TaskCompletionSource<object> _completedEvent = new TaskCompletionSource<object>();
        private DateTime _lastTxTime = DateTime.MinValue;
        private readonly TimeSpan _idleThreshold;
        private readonly Timer _keepAliveTimer;
        private readonly ISystemMessage _keepAliveMessage;
        private readonly Action<RpcResult> _commErrorHandler;
        private readonly Action _connectionRequestHandler;
        private readonly TxTransportFeed _feed;

        public TxPipeline_NoQueue(ContractDescriptor descriptor, Endpoint config, Action<RpcResult> commErrorHandler, Action connectionRequestHandler)
        {
            _commErrorHandler = commErrorHandler;
            _connectionRequestHandler = connectionRequestHandler;

            TaskQueue = config.TaskQueue;

            _buffer = new TxBuffer(_lockObj, config.TxBufferSegmentSize, descriptor.SerializationAdapter);
            _bufferSizeThreshold = config.TxBufferSegmentSize * 2;
            _buffer.SpaceFreed += _buffer_SpaceFreed;

            _feed = new TxTransportFeed(_buffer, commErrorHandler);

            if (config.IsKeepAliveEnabled)
            {
                _keepAliveTimer = new Timer(OnKeepAliveTimerTick, null, 100, 100);
                _buffer.OnDequeue += RefreshLastTxTime;
                _idleThreshold = config.KeepAliveThreshold;
                _keepAliveMessage = descriptor.SystemMessages.CreateHeartBeatMessage();
            }
        }

        private bool CanProcessUserMessage => _isUserMessagesEnabled && !_isProcessingItem && HasRoomForNextMessage;
        private bool CanProcessSystemMessage => _isStarted && !_isProcessingItem && HasRoomForNextMessage;
        private bool HasRoomForNextMessage => _buffer.DataSize < _bufferSizeThreshold;

        public TaskFactory TaskQueue { get; }

        public RpcResult TrySend(IMessage message)
        {
            lock (_lockObj)
            {
                if (_fault.Code != RpcRetCode.Ok)
                    return _fault;

                CheckConnectionFlags();

                while (!CanProcessUserMessage)
                {
                    Monitor.Wait(_lockObj);

                    if (_fault.Code != RpcRetCode.Ok)
                        return _fault;
                }

                _isProcessingItem = true;
                _buffer.Lock();
            }

            return ProcessMessage(message);
        }

#if NET5_0_OR_GREATER
        public ValueTask<RpcResult> TrySendAsync(IMessage message)
#else
        public Task<RpcResult> TrySendAsync(IMessage message)
#endif
        {
            lock (_lockObj)
            {
                if (_fault.Code != RpcRetCode.Ok)
                    return FwAdapter.WrappResult(_fault);

                CheckConnectionFlags();

                if (!CanProcessUserMessage)
                {
                    //var waitItem = new AsyncTryItem(message);
                    //_asyncQueue.Enqueue(waitItem);
                    //return FwAdapter.WrappResult(waitItem.Task);

                    return FwAdapter.WrappResult(_asyncGate.Enqueue(message, false, false));
                }
                else
                {
                    _isProcessingItem = true;
                    _buffer.Lock();
                }
            }

            return FwAdapter.WrappResult(ProcessMessage(message));
        }

#if NET5_0_OR_GREATER
        public ValueTask<RpcResult> SendSystemMessage(ISystemMessage message)
#else
        public Task<RpcResult> SendSystemMessage(ISystemMessage message)
#endif
        {
            lock (_lockObj)
            {
                if (_isClosing)
                    return FwAdapter.WrappResult(_fault);

                CheckConnectionFlags();

                if (!CanProcessSystemMessage)
                {
                    //var waitItem = new AsyncTryItem(message);
                    //_systemQueue.Enqueue(waitItem);
                    //return FwAdapter.WrappResult(waitItem.Task);

                    return FwAdapter.WrappResult(_asyncGate.Enqueue(message, false, true));
                }
                else
                {
                    _isProcessingItem = true;
                    _buffer.Lock();
                }
            }

            return FwAdapter.WrappResult(ProcessMessage(message));
        }

        private void CasuallySend(ISystemMessage message)
        {
            lock (_lockObj)
            {
                if (_isClosing || !CanProcessSystemMessage)
                    return;

                _isProcessingItem = true;
                _buffer.Lock();
            }

            ProcessMessage(message);
        }

#if NET5_0_OR_GREATER
        public ValueTask SendAsync(IMessage message)
#else
        public Task SendAsync(IMessage message)
#endif
        {
            lock (_lockObj)
            {
                _fault.ThrowIfNotOk();

                CheckConnectionFlags();

                if (!CanProcessUserMessage)
                {
                    //var waitItem = new AsyncThrowItem(message);
                    //_asyncQueue.Enqueue(waitItem);
                    //return FwAdapter.WrappResult((Task)waitItem.Task);

                    return FwAdapter.WrappResult((Task)_asyncGate.Enqueue(message, true, false));
                }
                else
                {
                    _isProcessingItem = true;
                    _buffer.Lock();
                }
            }

            ProcessMessage(message).ThrowIfNotOk();

            return FwAdapter.AsyncVoid;
        }

        public void Send(IMessage message)
        {
            TrySend(message).ThrowIfNotOk();
        }

        private void CheckConnectionFlags()
        {
            if (!_isStarted && !_isRequestedConnection)
            {
                _isRequestedConnection = true;
                RequestConnection();
            }
        }

        private async void RequestConnection()
        {
            // exit lock
            await TaskQueue.Dive();

            _connectionRequestHandler();
        }

        private RpcResult ProcessMessage(IMessage msg)
        {
            try
            {
                _buffer.WriteMessage(msg);
                return RpcResult.Ok;
            }
            catch (RpcException rex)
            {
                return OnTxError(rex.ToRpcResult());
            }
            catch (Exception ex)
            {
                return OnTxError(new RpcResult(RpcRetCode.SerializationError, ex.Message));
            }
            finally
            {
                lock (_lockObj)
                {
                    _isProcessingItem = false;

                    if (!_isClosing)
                        EnqueueNextItem();
                    else
                    {
                        if (_asyncGate.UserQueueSize == 0)
                            CompleteClose();
                    }
                }
            }
        }

        private RpcResult OnTxError(RpcResult error)
        {
            lock (_lockObj)
            {
                // TO DO: stop TxPipeline immediately
            }

            _commErrorHandler(error);

            return error;
        }

        //protected override SlimAwaitable<ArraySegment<byte>> DequeueNextSegment()
        //{
        //    return _buffer.DequeueNext();
        //}

        private void EnqueueNextItem()
        {
            var nextPending = _asyncGate.Dequeue(_isUserMessagesEnabled);

            if (nextPending != null)
            {
                _isProcessingItem = true;
                _buffer.Lock();

                TaskQueue.StartNew(p =>
                {
                    var task = (TxAsyncGate.Item)p;
                    task.OnResult(ProcessMessage(task.Message));
                }, nextPending);
            }
            else
            {
                Monitor.PulseAll(_lockObj);
            }
        }

        //private IPendingItem DequeuePending()
        //{
        //    if (_systemQueue.Count > 0)
        //        return _systemQueue.Dequeue();
        //    else if (_isUserMessagesEnabled && _asyncQueue.Count > 0)
        //        return _asyncQueue.Dequeue();
        //    else
        //        return null;
        //}

        public void Start(ByteTransport transport)
        {
            lock (_lockObj)
            {
                _isStarted = true;

                //Transport = transport;
                _feed.StartTransportWrite(transport);
                EnqueueNextItem();
            }
        }

        public void StartProcessingUserMessages()
        {
            lock (_lockObj)
            {
                if (_isStarted && !_isClosing)
                {
                    _isUserMessagesEnabled = true;
                    EnqueueNextItem();
                }
                else
                    throw new Exception("Invalid state!");
            }
        }

        public void StopProcessingUserMessages(RpcResult fault)
        {
            lock (_lockObj)
            {
                if (!_isClosing)
                {
                    _isUserMessagesEnabled = false;
                    _fault = fault;

                    Monitor.PulseAll(_lockObj);
                    _asyncGate.CancelUserItems(_fault);
                }
            }
        }

        public async Task Close(TimeSpan gracefulCloseTimeout)
        {
            await ClosePipeline(gracefulCloseTimeout);
            await _feed.WaitTransportWaitToEnd();
        }

        private Task ClosePipeline(TimeSpan gracefulCloseTimeout)
        {
            bool gracefulClose = gracefulCloseTimeout > TimeSpan.Zero;

            lock (_lockObj)
            {
                if (!_isClosing)
                {
                    _isClosing = true;

                    _keepAliveTimer?.Dispose();

                    Monitor.PulseAll(_lockObj);

                    if (!gracefulClose)
                        _asyncGate.CancelSysytemItems(_fault);

                    if (!_isProcessingItem)
                        CompleteClose();

                    if (gracefulClose)
                        _feed.AbortTransportWriteAfter(gracefulCloseTimeout);
                    else
                        _feed.AbortTransportWrite();
                }
            }

            return _completedEvent.Task;
        }

        private void CompleteClose()
        {
            _buffer.Close();
            _completedEvent.TrySetResult(true);
        }

        private void OnKeepAliveTimerTick(object state)
        {
            lock (_lockObj)
            {
                if (DateTime.UtcNow - _lastTxTime > _idleThreshold)
                    CasuallySend(_keepAliveMessage);
            }
        }

        private void RefreshLastTxTime()
        {
            _lastTxTime = DateTime.UtcNow;
        }

        private void _buffer_SpaceFreed(TxBuffer sender)
        {
            if (_isStarted && !_isProcessingItem)
                EnqueueNextItem();
        }
    }
}
