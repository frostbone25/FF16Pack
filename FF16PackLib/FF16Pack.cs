﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Vortice.DirectStorage;
using Syroot.BinaryData;

namespace FF16PackLib;

/// <summary>
/// FF16 Pack file. (Disposable object)
/// </summary>
public class FF16Pack : IDisposable
{
    public const uint MAGIC = 0x4B434150;
    public const int MAX_DECOMPRESSED_CHUNK_SIZE = 0x80000;

    public bool HeaderEncrypted { get; set; }
    public bool UseChunks { get; set; }

    private Dictionary<string, FF16PackFile> _files = [];
    private List<FF16PackDStorageChunk> _chunks { get; set; } = [];
    private Dictionary<long, FF16PackDStorageChunk> _offsetToChunk = [];

    private FileStream _stream;
    private HashSet<FF16PackDStorageChunk> _cachedChunks = [];

    private FF16Pack(FileStream stream)
    {
        _stream = stream;
    }

    private static IDStorageCompressionCodec _codec;
    static FF16Pack()
    {
        _codec = DirectStorage.DStorageCreateCompressionCodec(CompressionFormat.GDeflate, (uint)Environment.ProcessorCount);
    }

    public static FF16Pack Open(string path)
    {
        var fs = File.OpenRead(path);
        var fileBinStream = new BinaryStream(fs);

        if (fileBinStream.ReadUInt32() != MAGIC)
            throw new InvalidDataException("Not a FF16 Pack file");

        FF16Pack pack = new FF16Pack(fs);
        uint headerSize = fileBinStream.ReadUInt32();
        fileBinStream.Position = 0;
        byte[] header = fileBinStream.ReadBytes((int)headerSize);

        using (var ms = new MemoryStream(header))
        using (var bs = new BinaryStream(ms))
        {
            bs.Position = 8;
            uint numFiles = bs.ReadUInt32();
            pack.UseChunks = bs.ReadBoolean();
            pack.HeaderEncrypted = bs.ReadBoolean();
            ushort numChunks = bs.ReadUInt16();
            ulong packSize = bs.ReadUInt64();
            bs.Position = 0x18;

            if (pack.HeaderEncrypted)
                DecryptHeaderPart(header.AsSpan(0x18, 0x100));

            bs.Position = 0x118;
            ulong chunksTableOffset = bs.ReadUInt64();
            ulong stringsOffset = bs.ReadUInt64();
            ulong stringTableSize = bs.ReadUInt64();

            if (pack.HeaderEncrypted)
                DecryptHeaderPart(header.AsSpan((int)stringsOffset, (int)stringTableSize));

            for (int i = 0; i < numFiles; i++)
            {
                bs.Position = 0x400 + (i * 0x38);
                var file = new FF16PackFile();
                file.FromStream(bs);

                bs.Position = (long)file.FileNameOffset;
                string fileName = bs.ReadString(StringCoding.ZeroTerminated);
                pack._files.Add(fileName, file);
            }

            pack._chunks.Capacity = numChunks;
            for (uint i = 0; i < numChunks; i++)
            {
                bs.Position = (long)(chunksTableOffset + (i * (ulong)FF16PackDStorageChunk.GetSize()));
                var chunk = new FF16PackDStorageChunk();
                pack._offsetToChunk.Add(bs.Position, chunk);

                chunk.FromStream(bs);
                pack._chunks.Add(chunk);
            }
        }

        return pack;
    }

    public int GetNumFiles()
    {
        return _files.Count;
    }

    public int GetNumChunks()
    {
        return _chunks.Count;
    }

    public bool FileExists(string path)
    {
        return _files.ContainsKey(path);
    }

    public void ExtractAll(string outputDir)
    {
        int i = 0;
        foreach (KeyValuePair<string, FF16PackFile> file in _files)
        {
            Console.Write($"[{i + 1}/{_files.Count}] ");
            ExtractFile(file.Key, outputDir);
            i++;
        }
    }

    public void ExtractFile(string path, string outputDir)
    {
        if (!_files.TryGetValue(path, out FF16PackFile packFile))
            throw new FileNotFoundException("File not found in pack.");

        Console.Write($"Extracting '{path}' (0x{packFile.DecompressedFileSize:X} bytes)...\n");

        string outputPath = Path.Combine(Path.GetFullPath(outputDir), path);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        if (packFile.IsCompressed)
        {
            switch (packFile.CompressionFlags)
            {
                case FF16PackFileFlags.UseSharedChunk:
                    ExtractFileFromSharedChunk(packFile, outputPath);
                    break;
                case FF16PackFileFlags.UseMultipleChunks:
                    ExtractFileFromMultipleChunks(packFile, outputPath);
                    break;
                case FF16PackFileFlags.UseSpecificChunk:
                    ExtractFileFromSpecificChunk(packFile, outputPath);
                    break;
                case FF16PackFileFlags.None:
                    throw new ArgumentException($"Pack file '{path}' has compression flag but compression type flag is '{packFile.CompressionFlags}'..?");
                default:
                    throw new NotSupportedException($"Compression type {packFile.CompressionFlags} for file '{path}'");
            }
        }
        else
        {
            int size = (int)packFile.DecompressedFileSize;
            _stream.Position = (long)packFile.DataOffset;
            byte[] buffer = new byte[0x20000];

            using var outputStream = new FileStream(outputPath, FileMode.Create);
            while (size > 0)
            {
                int cnt = Math.Min(size, buffer.Length);
                _stream.Read(buffer, 0, cnt);
                outputStream.Write(buffer, 0, cnt);
                size -= cnt;
            }
        }
    }

    public void ListFiles(string outputPath)
    {
        using var sw = new StreamWriter(outputPath);
        foreach (var file in _files)
        {
            sw.WriteLine($"{file.Key} - hash:{file.Value.UnkHash_0x28:X8}, compressed: {file.Value.IsCompressed} ({file.Value.CompressionFlags}), " +
                $"dataOffset: 0x{file.Value.DataOffset:X16}, fileSize: 0x{file.Value.DecompressedFileSize:X8}, compressedFileSize: 0x{file.Value.CompressedFileSize:X8}");
        }
    }

    private void ExtractFileFromMultipleChunks(FF16PackFile packFile, string outputPath)
    {
        _stream.Position = (long)packFile.ChunkDefOffset;
        uint numChunks = _stream.ReadUInt32();
        uint size = _stream.ReadUInt32();
        uint[] chunkOffsets = _stream.ReadUInt32s((int)numChunks);

        byte[] compBuffer = ArrayPool<byte>.Shared.Rent(MAX_DECOMPRESSED_CHUNK_SIZE);
        byte[] decompBuffer = ArrayPool<byte>.Shared.Rent(MAX_DECOMPRESSED_CHUNK_SIZE);

        long remSize = (long)packFile.DecompressedFileSize;
        var outputStream = new FileStream(outputPath, FileMode.Create);
        for (int i = 0; i < numChunks; i++)
        {
            int chunkCompSize = i < numChunks - 1 ?
                  (int)(chunkOffsets[i + 1] - chunkOffsets[i])
                : (int)packFile.CompressedFileSize - (int)chunkOffsets[i];
            int chunkDecompSize = (int)Math.Min(remSize, MAX_DECOMPRESSED_CHUNK_SIZE);

            _stream.Position = (long)(packFile.DataOffset + chunkOffsets[i]);
            _stream.Read(compBuffer, 0, chunkCompSize);

            unsafe
            {
                fixed (byte* buffer = compBuffer)
                fixed (byte* buffer2 = decompBuffer)
                {
                    _codec.DecompressBuffer((nint)buffer, chunkCompSize, (nint)buffer2, chunkDecompSize, chunkDecompSize);
                }
            }
            outputStream.Write(decompBuffer, 0, chunkDecompSize);
            remSize -= chunkDecompSize;
        }

        ArrayPool<byte>.Shared.Return(compBuffer);
        ArrayPool<byte>.Shared.Return(decompBuffer);

        if (remSize != 0)
            throw new IOException($"File '{outputPath}' was not extracted completely (numChunks: {numChunks}, rem: {remSize})");
    }

    private void ExtractFileFromSpecificChunk(FF16PackFile packFile, string outputPath)
    {
        _stream.Position = (long)packFile.DataOffset;

        byte[] compBuffer = ArrayPool<byte>.Shared.Rent((int)packFile.CompressedFileSize);
        byte[] decompBuffer = ArrayPool<byte>.Shared.Rent((int)packFile.DecompressedFileSize);
        _stream.Read(compBuffer, 0, (int)packFile.CompressedFileSize);

        unsafe
        {
            fixed (byte* buffer = compBuffer)
            fixed (byte* buffer2 = decompBuffer)
            {
                _codec.DecompressBuffer((nint)buffer, (int)packFile.CompressedFileSize, (nint)buffer2, (int)packFile.DecompressedFileSize, (int)packFile.DecompressedFileSize);
            }
        }

        using var outputStream = new FileStream(outputPath, FileMode.Create);
        outputStream.Write(decompBuffer, 0, (int)packFile.DecompressedFileSize);

        ArrayPool<byte>.Shared.Return(compBuffer);
        ArrayPool<byte>.Shared.Return(decompBuffer);
    }

    private void ExtractFileFromSharedChunk(FF16PackFile packFile, string outputPath)
    {
        FF16PackDStorageChunk chunk = _offsetToChunk[(long)packFile.ChunkDefOffset];

        if (!_cachedChunks.Contains(chunk))
        {
            if (_cachedChunks.Count >= 20)
            {
                var chunkToRemove = _cachedChunks.First();
                ArrayPool<byte>.Shared.Return(chunkToRemove.CachedBuffer);
                chunkToRemove.CachedBuffer = null;

                _cachedChunks.Remove(chunkToRemove);
            }
        }

        _stream.Position = (long)chunk.DataOffset;

        if (chunk.CachedBuffer is null)
        {
            byte[] compBuffer = ArrayPool<byte>.Shared.Rent((int)chunk.CompressedChunkSize);
            byte[] decompBuffer = ArrayPool<byte>.Shared.Rent((int)chunk.DecompressedSize);
            _stream.Read(compBuffer, 0, (int)chunk.CompressedChunkSize);

            unsafe
            {

                fixed (byte* buffer = compBuffer)
                fixed (byte* buffer2 = decompBuffer)
                {
                    _codec.DecompressBuffer((nint)buffer, (int)chunk.CompressedChunkSize, (nint)buffer2, (int)chunk.DecompressedSize, (int)chunk.DecompressedSize);
                }
            }

            chunk.CachedBuffer = decompBuffer;
            ArrayPool<byte>.Shared.Return(compBuffer);
        }

        using var outputStream = new FileStream(outputPath, FileMode.Create);
        outputStream.Write(chunk.CachedBuffer, (int)packFile.DataOffset, (int)packFile.DecompressedFileSize);

        _cachedChunks.Add(chunk);
    }

    private static void DecryptHeaderPart(Span<byte> data)
    {
        // TODO maybe: just use an array with the key
        Span<byte> cur = data;

        while (cur.Length >= 8)
        {
            Span<ulong> u64s = MemoryMarshal.Cast<byte, ulong>(cur);
            u64s[0] = u64s[0] ^ (u64s[0] ^ ~u64s[0]) & 0x49D18FC870F3824EUL;
            cur = cur[8..];
        }

        if (cur.Length >= 4)
        {
            Span<uint> u32s = MemoryMarshal.Cast<byte, uint>(cur);
            u32s[0] = u32s[0] ^ (u32s[0] ^ ~u32s[0]) & 0x70F3824E;
            cur = cur[4..];
        }

        if (cur.Length >= 2)
        {
            Span<ushort> u16s = MemoryMarshal.Cast<byte, ushort>(cur);
            u16s[0] = (ushort)(~u16s[0] ^ (u16s[0] ^ ~u16s[0]) & 0x7DB1); // NOTE: ~ on the first variable - maybe to intentionally throw people off? ~0x7DB1 = 0x824E
            cur = cur[2..];
        }

        if (cur.Length >= 1)
        {
            cur[0] = (byte)(cur[0] ^ (cur[0] ^ ~cur[0]) & 0x4E);
        }
    }

    public void Dispose()
    {
        ((IDisposable)_stream).Dispose();
        GC.SuppressFinalize(this);
    }
}
