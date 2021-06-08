﻿// Copyright © 2021 Soft-Fx. All rights reserved.
// Author: Andrei Hilevich
//
// This Source Code Form is subject to the terms of the Mozilla
// Public License, v. 2.0. If a copy of the MPL was not distributed
// with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SharpRpc
{
    internal partial class TxBuffer : IBufferWriter<byte>, MessageWriter
    {
        private readonly object _lockObj;
        private readonly StreamProxy _streamProxy;
        private readonly Queue<ArraySegment<byte>> _completeSegments = new Queue<ArraySegment<byte>>();
        private readonly MemoryManager _memManager;
        private readonly int _minAllocSize = 64;
        private readonly MessageMarker _marker;
        private DequeueRequest _dequeueWaitHandle;
        //private readonly Action _dataArrivedEvent;
        private readonly IRpcSerializer _serializer;
        private ArraySegment<byte> _dequeuedSegment;
        private bool _isClosed;

        public TxBuffer(object lockObj, int segmentSize, IRpcSerializer serializer)
        {
            _lockObj = lockObj;

            //if (segmentSize > ushort.MaxValue)
            //    throw new ArgumentException("Segment size must be less than " + ushort.MaxValue + ".");

            _serializer = serializer;

            //_dataArrivedEvent = dataArrivedCallback;

            _memManager = new MemoryManager(segmentSize, 5);
            //_minAllocSize = minSizeHint;
            _streamProxy = new StreamProxy(this);

            _marker = new MessageMarker(this);

            AllocNewSegment();
        }

        public bool IsCurrentSegmentLocked { get; private set; }
        public bool IsDataAvailable => IsCurrentDataAvailable || HasCompletedSegments;

        public int DataSize { get; private set; }

        private byte[] CurrentSegment { get; set; }
        private int CurrentOffset { get; set; }

        public event Action<TxBuffer> SpaceFreed;

        private bool IsCurrentDataAvailable => !IsCurrentSegmentLocked && CurrentOffset > 0;
        private bool HasCompletedSegments => _completeSegments.Count > 0;
        private int SegmentSize => _memManager.SegmentSize;

        public event Action OnDequeue;

        // shoult be called under lock
        public void Lock()
        {
            //lock (_lockObj)
            IsCurrentSegmentLocked = true;
        }

        //public DequeueRequest Unlock()
        //{
        //    IsCurrentSegmentLocked = false;
        //    return SignalDataAvailable();
        //}

        public void WriteMessage(IMessage message)
        {
            //lock (_lockObj)
            //    IsCurrentSegmentLocked = true;

            _marker.OnMessageStart();

            if (message is IPrebuiltMessage mmsg)
                mmsg.WriteTo(0, this);
            else
                _serializer.Serialize(message, this);

            _marker.OnMessageEnd();

            DequeueRequest toSignal = null;

            lock (_lockObj)
            {
                IsCurrentSegmentLocked = false;
                toSignal = SignalDataAvailable();
            }

            toSignal?.Signal();
        }

        // shoult be called under lock
        public void Close()
        {
            _isClosed = true;

            var cpy = _dequeueWaitHandle;
            _dequeueWaitHandle = null;
            cpy?.TrySetResult(new ArraySegment<byte>());
        }

        //public void ReleaseLock()
        //{
        //    //IsCurrentSegmentLocked = false;

        //    DequeueRequest toSignal = null;

        //    //lock (_lockObj)
        //    {
        //        IsCurrentSegmentLocked = false;
        //        toSignal = SignalDataAvailable();
        //    }

        //    toSignal?.Signal();
        //}

        //public void StartMessageWrite(MessageHeader header)
        //{
        //    _marker.OnMessageStart(header);
        //}

        //public void EndMessageWrite()
        //{
        //    _marker.OnMessageEnd();
        //}

        public ValueTask<ArraySegment<byte>> DequeueNext()
        {
            lock (_lockObj)
            {
                if (_dequeuedSegment != null)
                {
                    _memManager.FreeSegment(_dequeuedSegment);
                    _dequeuedSegment = null;
                }

                var hasCurrentData = IsCurrentDataAvailable;

                if (HasCompletedSegments || hasCurrentData)
                {
                    var result = Dequeue();
                    SpaceFreed?.Invoke(this);
                    return new ValueTask<ArraySegment<byte>>(result);
                }
                else if (_isClosed)
                    return new ValueTask<ArraySegment<byte>>(new ArraySegment<byte>());
                else
                {
                    _dequeueWaitHandle = new DequeueRequest();
                    return new ValueTask<ArraySegment<byte>>(_dequeueWaitHandle.Task);
                }
            }
        }

        //public void ReturnSegments(List<ArraySegment<byte>> container)
        //{
        //    foreach (var segment in container)
        //        _memManager.FreeSegment(segment);
        //}

        private DequeueRequest SignalDataAvailable()
        {
            var cpy = _dequeueWaitHandle;
            _dequeueWaitHandle = null;

            if (cpy != null)
                cpy.Result = Dequeue();

            return cpy;
        }

        private ArraySegment<byte> Dequeue()
        {
            if (_completeSegments.Count == 0)
                CompleteCurrentSegment();

            _dequeuedSegment = _completeSegments.Dequeue();
            DataSize -= _dequeuedSegment.Count;

            OnDequeue?.Invoke();

            return _dequeuedSegment;
        }

        #region IBufferWriter implementation

        public void Advance(int count)
        {
            MoveOffset(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureSpace(sizeHint);
            return new Memory<byte>(CurrentSegment, CurrentOffset, SegmentSize - CurrentOffset);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureSpace(sizeHint);
            return new Span<byte>(CurrentSegment, CurrentOffset, SegmentSize - CurrentOffset);
        }

        #endregion

        private void EnsureSpace(int sizeHint)
        {
            if (sizeHint <= _minAllocSize)
                sizeHint = _minAllocSize;

            var spaceInCurrentSegment = SegmentSize - CurrentOffset;

            if (spaceInCurrentSegment < sizeHint)
                CompleteCurrentSegment();

            _marker.OnAlloc();
        }

        private void CompleteCurrentSegment()
        {
            _marker.OnSegmentClose();

            DequeueRequest toSignal = null;

            lock (_lockObj)
            {
                _completeSegments.Enqueue(new ArraySegment<byte>(CurrentSegment, 0, CurrentOffset));

                AllocNewSegment();

                toSignal = SignalDataAvailable();
            }

            toSignal?.Signal();
        }

        private void AllocNewSegment()
        {
            CurrentSegment = _memManager.AllocateSegment();
            CurrentOffset = 0;
        }

        private void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                EnsureSpace(_minAllocSize);

                var spaceLeft = SegmentSize - CurrentOffset;
                var copySize = Math.Min(spaceLeft, count);
                Buffer.BlockCopy(buffer, offset, CurrentSegment, CurrentOffset, copySize);

                CurrentOffset += copySize;
                offset += copySize;
                count -= copySize;
            }
        }

        private void MoveOffset(int size)
        {
            CurrentOffset += size;
            DataSize += size;
        }

        #region MessageWriter implementation

        IBufferWriter<byte> MessageWriter.ByteBuffer => this;
        System.IO.Stream MessageWriter.ByteStream => _streamProxy;

        #endregion

        public class DequeueRequest : TaskCompletionSource<ArraySegment<byte>>
        {
            public ArraySegment<byte> Result { get; set; }

            public void Signal()
            {
                SetResult(Result);
            }
        }
    }
}
