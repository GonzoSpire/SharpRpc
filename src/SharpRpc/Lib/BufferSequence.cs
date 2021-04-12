﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace SharpRpc.Lib
{
    internal class BufferSequence<T>
    {
        private readonly List<Segment> _cachedSegments = new List<Segment>();
        private int _lastIndex = -1;
        private int _totalSize;

        public int Count => _totalSize;

        public ReadOnlySequence<T> GetSequence()
        {
            var first = _cachedSegments[0];
            var last = _cachedSegments[_lastIndex];

            return new ReadOnlySequence<T>(first, 0, last, last.Memory.Length);
        }

        public void Clear()
        {
            for (int i = 0; i <= _lastIndex; i++)
                _cachedSegments[i].Clear();

            _totalSize = 0;
            _lastIndex = -1;
        }

        public void Add(ArraySegment<T> segment)
        {
            Add(segment.Array, segment.Offset, segment.Count);
        }

        public void Add(T[] data, int offset, int count)
        {
            if (_lastIndex + 1 >= _cachedSegments.Count)
                _cachedSegments.Add(new Segment());

            var prevSegment = _lastIndex >= 0 ? _cachedSegments[_lastIndex] : null;

            var currentSegment = _cachedSegments[++_lastIndex];
            currentSegment.SetData(new ArraySegment<T>(data, offset, count));
            currentSegment.SetSize(_totalSize);

            _totalSize += count;
            prevSegment?.SetNextSegment(currentSegment);
        }

        public void AddRange(IEnumerable<ArraySegment<T>> segments)
        {
            foreach (var segment in segments)
                Add(segment);
        }

        private class Segment : ReadOnlySequenceSegment<T>
        {
            public void Init(ArraySegment<T> buffer, Segment nextSegment, int size)
            {
                Memory = buffer;
                Next = nextSegment;
                RunningIndex = size;
            }

            public void SetData(ArraySegment<T> data)
            {
                Memory = data;
            }

            public void SetNextSegment(Segment next)
            {
                Next = next;
            }

            public void SetSize(long size)
            {
                RunningIndex = size;
            }

            public void Clear()
            {
                RunningIndex = 0;
                Memory = null;
                Next = null;
            }
        }
    }
}
