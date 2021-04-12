﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SharpRpc
{
    internal class MessageParser
    {
        //private readonly List<ArraySegment<byte>> _parsedSegments = new List<ArraySegment<byte>>();
        private readonly List<ArraySegment<byte>> _messageFragments = new List<ArraySegment<byte>>();
        private readonly HeaderParser _headerParser = new HeaderParser();
        private States _phase = States.Header;
        private ushort _specifiedChunkSize;
        private ArraySegment<byte> _segment; 
        private int _segmentOffset;
        private int _currentChunkSize;
        private int _currentMsgSize;
        private bool _isLastChunk;

        public IReadOnlyList<ArraySegment<byte>> MessageBody => _messageFragments;

        public void SetNextSegment(ArraySegment<byte> segment)
        {
            _segment = segment;
            _segmentOffset = 0;
        }

        public RetCodes ParseFurther()
        {
            while (_segmentOffset < _segment.Count)
            {
                if (_phase == States.EndOfMessage)
                {
                    //_parsedSegments.Clear();
                    _messageFragments.Clear();
                    _currentMsgSize = 0;
                    _phase = States.Header;
                }

                if (_phase == States.Header || _phase == States.ChunkHeader)
                {
                    var rCode = _headerParser.ParseNextByte(_segment[_segmentOffset++]);

                    if (rCode == ParserRetCode.Error)
                        return RetCodes.InvalidHeader;

                    if (rCode == ParserRetCode.Complete)
                    {
                        if (_phase == States.ChunkHeader)
                        {
                            // TO DO : check
                        }

                        _phase = States.Body;
                        _specifiedChunkSize = _headerParser.ChunkSize;
                        _currentChunkSize = 0;
                        _isLastChunk = _headerParser.IsEoM;
                    }
                }
                else if (_phase == States.Body)
                {
                    var dataLeftInSegment = _segment.Count - _segmentOffset;
                    var fragmentSize = Math.Min(dataLeftInSegment, _specifiedChunkSize - _currentChunkSize);
                    AddMessageFragment(_segment, _segmentOffset, fragmentSize);
                    _currentChunkSize += fragmentSize;
                    _currentMsgSize += fragmentSize;
                    _segmentOffset += fragmentSize;

                    if (_currentChunkSize == _specifiedChunkSize)
                    {
                        if (_isLastChunk)
                        {
                            _phase = States.EndOfMessage;
                            return RetCodes.MessageParsed;
                        }
                        else
                        {
                            _phase = States.ChunkHeader;
                        }
                    }
                }
            }

            return RetCodes.EndOfSegment;
        }

        private void AddMessageFragment(ArraySegment<byte> segment, int offset, int length)
        {
            _messageFragments.Add(new ArraySegment<byte>(segment.Array, segment.Offset + offset, length));
        }

        private enum States { EndOfMessage, Header, Body, ChunkHeader  }

        public enum RetCodes { MessageParsed, EndOfSegment, InvalidHeader }

#if DEBUG
        public string MessagBodyString
        {
            get
            {
                var builder = new StringBuilder();

                foreach (var fragment in MessageBody)
                {
                    foreach (var b in fragment)
                    {
                        if (builder.Length == 0)
                            builder.Append('{');
                        else
                            builder.Append(',');

                        builder.Append(b);
                    }
                }

                builder.Append('}');

                return builder.ToString();
            }
        }
#endif
    }
}
