﻿/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2014, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AudioChord.Parsers
{
    /// <summary>
    ///     Provides an <see cref="Parsers.IContainerReader" /> implementation for basic Ogg files.
    /// </summary>
    internal class OggContainerReader : IContainerReader
    {
        private readonly Crc _crc = new Crc();
        private readonly List<int> _disposedStreamSerials;
        private readonly Dictionary<int, PacketReader> _packetReaders;
        private readonly byte[] _readBuffer = new byte[65025]; // up to a full page of data (but no more!)
        private readonly BufferedReadStream _stream;
        private long _containerBits;
        private long _nextPageOffset;

        /// <summary>
        ///     Creates a new instance with the specified stream.  Optionally sets to close the stream when disposed.
        /// </summary>
        /// <param name="stream">The stream to read.</param>
        /// <param name="closeOnDispose">
        ///     <c>True</c> to close the stream when <see cref="Dispose" /> is called, otherwise
        ///     <c>False</c>.
        /// </param>
        public OggContainerReader(Stream stream, bool closeOnDispose)
        {
            _packetReaders = new Dictionary<int, PacketReader>();
            _disposedStreamSerials = new List<int>();

            _stream = stream as BufferedReadStream ?? new BufferedReadStream(stream) {CloseBaseStream = closeOnDispose};
        }

        /// <summary>
        ///     Gets the list of stream serials found in the container so far.
        /// </summary>
        public int[] StreamSerials => _packetReaders.Keys.ToArray();

        /// <summary>
        ///     Event raised when a new logical stream is found in the container.
        /// </summary>
        public event EventHandler<NewStreamEventArgs> NewStream;

        /// <summary>
        ///     Initializes the container and finds the first stream.
        /// </summary>
        /// <returns><see langword="true" /> if a valid logical stream is found, otherwise <see langword="false" />.</returns>
        public bool Init()
        {
            _stream.TakeLock();
            try
            {
                return GatherNextPage() != -1;
            }
            finally
            {
                _stream.ReleaseLock();
            }
        }

        /// <summary>
        ///     Disposes this instance.
        /// </summary>
        public void Dispose()
        {
            // don't use _packetReaders directly since that'll change the enumeration...
            foreach (int streamSerial in StreamSerials) _packetReaders[streamSerial].Dispose();

            _nextPageOffset = 0L;
            _containerBits = 0L;
            WasteBits = 0L;

            _stream.Dispose();
        }

        /// <summary>
        ///     Finds the next new stream in the container.
        /// </summary>
        /// <returns><see langword="true" /> if a new stream was found, otherwise <see langword="false" />.</returns>
        /// <exception cref="InvalidOperationException"><see cref="CanSeek" /> is <c>False</c>.</exception>
        public bool FindNextStream()
        {
            if (!CanSeek) throw new InvalidOperationException();

            // goes through all the pages until the serial count increases
            int cnt = _packetReaders.Count;
            while (_packetReaders.Count == cnt)
            {
                _stream.TakeLock();
                try
                {
                    // acquire & release the lock every pass so we don't block any longer than necessary
                    if (GatherNextPage() == -1) break;
                }
                finally
                {
                    _stream.ReleaseLock();
                }
            }

            return cnt > _packetReaders.Count;
        }

        /// <summary>
        ///     Gets the number of pages that have been read in the container.
        /// </summary>
        public int PagesRead { get; private set; }

        /// <summary>
        ///     Retrieves the total number of pages in the container.
        /// </summary>
        /// <returns>The total number of pages.</returns>
        /// <exception cref="InvalidOperationException"><see cref="CanSeek" /> is <c>False</c>.</exception>
        public int GetTotalPageCount()
        {
            if (!CanSeek) throw new InvalidOperationException();

            // just read pages until we can't any more...
            while (true)
            {
                _stream.TakeLock();
                try
                {
                    // acquire & release the lock every pass so we don't block any longer than necessary
                    if (GatherNextPage() == -1) break;
                }
                finally
                {
                    _stream.ReleaseLock();
                }
            }

            return PagesRead;
        }

        /// <summary>
        ///     Gets whether the container supports seeking.
        /// </summary>
        public bool CanSeek => _stream.CanSeek;

        /// <summary>
        ///     Gets the number of bits in the container that are not associated with a logical stream.
        /// </summary>
        public long WasteBits { get; private set; }

        /// <summary>
        ///     Gets the <see cref="Parsers.IPacketProvider" /> instance for the specified stream serial.
        /// </summary>
        /// <param name="streamSerial">The stream serial to look for.</param>
        /// <returns>An <see cref="Parsers.IPacketProvider" /> instance.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The specified stream serial was not found.</exception>
        public IPacketProvider GetStream(int streamSerial)
        {
            PacketReader provider;
            if (!_packetReaders.TryGetValue(streamSerial, out provider))
                throw new ArgumentOutOfRangeException("streamSerial");
            return provider;
        }

        private PageHeader ReadPageHeader(long position)
        {
            // set the stream's position
            _stream.Seek(position, SeekOrigin.Begin);

            // header
            // NB: if the stream didn't have an EOS flag, this is the most likely spot for the EOF to be found...
            if (_stream.Read(_readBuffer, 0, 27) != 27) return null;

            // capture signature ("OggS")
            if (_readBuffer[0] != 0x4f || _readBuffer[1] != 0x67 || _readBuffer[2] != 0x67 || _readBuffer[3] != 0x53)
            {
                Debug.WriteLine("No OggS signature found in header");
                return null;
            }

            // check the stream version
            if (_readBuffer[4] != 0)
            {
                Debug.WriteLine("Ogg stream version does not match expected value 0");
                return null;
            }

            // start populating the header
            PageHeader? hdr = new PageHeader();

            // bit flags
            hdr.Flags = (PageFlags) _readBuffer[5];

            // granulePosition
            hdr.GranulePosition = BitConverter.ToInt64(_readBuffer, 6);

            // stream serial
            hdr.StreamSerial = BitConverter.ToInt32(_readBuffer, 14);

            // sequence number
            hdr.SequenceNumber = BitConverter.ToInt32(_readBuffer, 18);

            // save off the CRC
            uint crc = BitConverter.ToUInt32(_readBuffer, 22);

            // start calculating the CRC value for this page
            _crc.Reset();
            for (int i = 0; i < 22; i++) _crc.Update(_readBuffer[i]);
            _crc.Update(0);
            _crc.Update(0);
            _crc.Update(0);
            _crc.Update(0);
            _crc.Update(_readBuffer[26]);

            // figure out the length of the page
            int segCnt = _readBuffer[26];
            if (_stream.Read(_readBuffer, 0, segCnt) != segCnt)
            {
                Debug.WriteLine("Page header reports that there are " + segCnt +
                                " lacing segments, but not that many bytes were found in the buffer!");
                return null;
            }

            List<int>? packetSizes = new List<int>(segCnt);

            int size = 0, idx = 0;
            for (int i = 0; i < segCnt; i++)
            {
                byte temp = _readBuffer[i];
                _crc.Update(temp);

                if (idx == packetSizes.Count) packetSizes.Add(0);
                packetSizes[idx] += temp;
                if (temp < 255)
                {
                    ++idx;
                    hdr.LastPacketContinues = false;
                }
                else
                {
                    hdr.LastPacketContinues = true;
                }

                size += temp;
            }

            hdr.PacketSizes = packetSizes.ToArray();
            hdr.DataOffset = position + 27 + segCnt;

            // now we have to go through every byte in the page
            if (_stream.Read(_readBuffer, 0, size) != size)
            {
                Debug.WriteLine("Could not read lacing segment from payload: Insufficient bytes");
                return null;
            }

            for (int i = 0; i < size; i++) _crc.Update(_readBuffer[i]);

            //Debug.WriteLine("Calculated CRC for page is " + _crc.Value + " (expecting " + crc + ")");
            if (_crc.Test(crc))
            {
                _containerBits += 8 * (27 + segCnt);
                ++PagesRead;
                return hdr;
            }

            Debug.WriteLine("CRC validation failed for page; assuming it is corrupted");
            return null;
        }

        private PageHeader FindNextPageHeader()
        {
            long startPos = _nextPageOffset;

            bool isResync = false;
            PageHeader hdr;
            while ((hdr = ReadPageHeader(startPos)) == null)
            {
                isResync = true;
                WasteBits += 8;
                _stream.Position = ++startPos;

                int cnt = 0;
                do
                {
                    int b = _stream.ReadByte();
                    if (b == 0x4f)
                    {
                        if (_stream.ReadByte() == 0x67 && _stream.ReadByte() == 0x67 && _stream.ReadByte() == 0x53)
                        {
                            // found it!
                            startPos += cnt;
                            break;
                        }

                        _stream.Seek(-3, SeekOrigin.Current);
                    }
                    else if (b == -1)
                    {
                        Debug.WriteLine("End of stream reached while looking for next page");
                        return null;
                    }

                    WasteBits += 8;
                } while (++cnt < 65536
                ); // we will only search through 64KB of data to find the next sync marker.  if it can't be found, we have a badly corrupted stream.

                if (cnt == 65536)
                {
                    Debug.WriteLine(
                        "Could not find the next page after searching through 64Kb of data. Assuming the stream is badly corrupted");
                    return null;
                }
            }

            hdr.IsResync = isResync;

            _nextPageOffset = hdr.DataOffset;
            for (int i = 0; i < hdr.PacketSizes.Length; i++) _nextPageOffset += hdr.PacketSizes[i];

            return hdr;
        }

        private bool AddPage(PageHeader header)
        {
            // get our packet reader (create one if we have to)
            PacketReader packetReader;
            if (!_packetReaders.TryGetValue(header.StreamSerial, out packetReader))
                packetReader = new PacketReader(this, header.StreamSerial);

            // save off the container bits
            packetReader.ContainerBits += _containerBits;
            _containerBits = 0;

            // get our flags prepped
            bool isContinued = header.PacketSizes.Length == 1 && header.LastPacketContinues;
            bool isContinuation = (header.Flags & PageFlags.ContinuesPacket) == PageFlags.ContinuesPacket;
            bool isEOS = false;
            bool isResync = header.IsResync;

            // add all the packets, making sure to update flags as needed
            long dataOffset = header.DataOffset;
            int count = header.PacketSizes.Length;
            foreach (int size in header.PacketSizes)
            {
                Packet? packet = new Packet(this, dataOffset, size)
                {
                    PageGranulePosition = header.GranulePosition,
                    IsEndOfStream = isEOS,
                    PageSequenceNumber = header.SequenceNumber,
                    IsContinued = isContinued,
                    IsContinuation = isContinuation,
                    IsResync = isResync
                };
                packetReader.AddPacket(packet);

                // update the offset into the stream for each packet
                dataOffset += size;

                // only the first packet in a page can be a continuation or resync
                isContinuation = false;
                isResync = false;

                // only the last packet in a page can be continued or flagged end of stream
                if (--count == 1)
                {
                    isContinued = header.LastPacketContinues;
                    isEOS = (header.Flags & PageFlags.EndOfStream) == PageFlags.EndOfStream;
                }
            }

            // if the packet reader list doesn't include the serial in question, add it to the list and indicate a new stream to the caller
            if (!_packetReaders.ContainsKey(header.StreamSerial))
            {
                _packetReaders.Add(header.StreamSerial, packetReader);
                return true;
            }

            // otherwise, indicate an existing stream to the caller
            return false;
        }

        private int GatherNextPage()
        {
            while (true)
            {
                // get our next header
                PageHeader? header = FindNextPageHeader();
                if (header is null) return -1;

                // if it's in a disposed stream, grab the next page instead
                if (_disposedStreamSerials.Contains(header.StreamSerial)) continue;

                // otherwise, add it
                if (AddPage(header))
                {
                    EventHandler<NewStreamEventArgs>? callback = NewStream;
                    if (callback != null)
                    {
                        NewStreamEventArgs? ea = new NewStreamEventArgs(_packetReaders[header.StreamSerial]);
                        callback(this, ea);
                        if (ea.IgnoreStream)
                        {
                            _packetReaders[header.StreamSerial].Dispose();
                            continue;
                        }
                    }
                }

                return header.StreamSerial;
            }
        }

        // packet reader bits...
        internal void DisposePacketReader(PacketReader packetReader)
        {
            _disposedStreamSerials.Add(packetReader.StreamSerial);
            _packetReaders.Remove(packetReader.StreamSerial);
        }

        internal int PacketReadByte(long offset)
        {
            _stream.TakeLock();
            try
            {
                _stream.Position = offset;
                return _stream.ReadByte();
            }
            finally
            {
                _stream.ReleaseLock();
            }
        }

        internal void PacketDiscardThrough(long offset)
        {
            _stream.TakeLock();
            try
            {
                _stream.DiscardThrough(offset);
            }
            finally
            {
                _stream.ReleaseLock();
            }
        }

        internal void GatherNextPage(int streamSerial)
        {
            if (!_packetReaders.ContainsKey(streamSerial)) throw new ArgumentOutOfRangeException("streamSerial");

            int nextSerial;
            do
            {
                _stream.TakeLock();
                try
                {
                    if (_packetReaders[streamSerial].HasEndOfStream) break;

                    nextSerial = GatherNextPage();
                    if (nextSerial == -1)
                    {
                        foreach (KeyValuePair<int, PacketReader> reader in _packetReaders)
                            if (!reader.Value.HasEndOfStream)
                                reader.Value.SetEndOfStream();
                        break;
                    }
                }
                finally
                {
                    _stream.ReleaseLock();
                }
            } while (nextSerial != streamSerial);
        }


        // private implmentation bits
        private class PageHeader
        {
            public int StreamSerial { get; set; }
            public PageFlags Flags { get; set; }
            public long GranulePosition { get; set; }
            public int SequenceNumber { get; set; }
            public long DataOffset { get; set; }
            public int[] PacketSizes { get; set; }
            public bool LastPacketContinues { get; set; }
            public bool IsResync { get; set; }
        }
    }
}