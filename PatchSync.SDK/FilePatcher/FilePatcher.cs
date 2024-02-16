using System.IO.Hashing;
using PatchSync.Common;
using PatchSync.Common.Hashing;
using PatchSync.Common.Signatures;

namespace PatchSync.SDK;

public static class FilePatcher
{
    public static PatchSlice[] GetPatchDeltas(
        Stream inputStream, // File being processed
        SignatureFile signatureFile,
        Action<FileProcessingResult>? callback = null
    )
    {
        var length = inputStream.Length;
        var chunks = signatureFile.Chunks;
        var chunkSize = signatureFile.ChunkSize; // Should be already rounded

        var processingResult = new FileProcessingResult();
        processingResult.Started(length);
        callback?.Invoke(processingResult);

        var slices = new PatchSlice?[chunks.Length];

        // Special case where the local file is smaller than a chunk
        if (length < chunkSize)
        {
            for (var i = 0; i < chunks.Length; i++)
            {
                slices[i] = new PatchSlice(PatchSliceLocation.RemoteSlice, i * chunkSize);
            }

            return slices;
        }

        // rolling hash -> index in signature file array
        var chunksByRollingHash = new Dictionary<uint, List<int>>();
        for (var i = 0; i < chunks.Length; i++)
        {
            var signatureFileChunk = chunks[i];
            var rollingHash = signatureFileChunk.RollingHash;
            if (!chunksByRollingHash.TryGetValue(rollingHash, out var list))
            {
                chunksByRollingHash[rollingHash] = list = [];
            }

            bool found = false;
            foreach (var index in list)
            {
                // We can have two chunks with the same exactly rolling/hash
                if (chunks[index] == signatureFileChunk)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                list.Add(i);
            }
        }

        var xxHash3 = new XxHash3();
        var chunkBuffer = new byte[chunkSize];

        var checksum = 1u;

        var bufferIndex = 0;

        // Find the existing chunks
        for (var i = 0; i < length; i++)
        {
            if (i < chunkSize)
            {
                if (inputStream.Read(chunkBuffer, 0, chunkBuffer.Length) != chunkBuffer.Length)
                {
                    throw new Exception("Could not read the entire chunk.");
                }

                checksum = Adler32RollingChecksum.Calculate(chunkBuffer);
                var chunkSizeMinusOne = chunkSize - 1;
                i += chunkSizeMinusOne; // -1 cause we have a i++ at the end of the for loop

                processingResult.InProgress(chunkSizeMinusOne);
                callback?.Invoke(processingResult);
            }
            else
            {
                checksum = Adler32RollingChecksum.Rotate(
                    checksum,
                    chunkBuffer[bufferIndex],
                    chunkBuffer[bufferIndex++] = (byte)inputStream.ReadByte(),
                    chunkSize
                );

                // Circular buffer
                if (bufferIndex >= chunkSize)
                {
                    bufferIndex = 0;
                }
            }

            // Check if we have an existing chunk, if not, continue to the next byte
            if (!chunksByRollingHash.TryGetValue(checksum, out var list))
            {
                processingResult.InProgress(1);
                callback?.Invoke(processingResult);
                continue;
            }

            // We have a potential match, so we need to check the hash
            xxHash3.Append(chunkBuffer.AsSpan(bufferIndex, chunkSize - bufferIndex));
            if (bufferIndex != 0)
            {
                xxHash3.Append(chunkBuffer.AsSpan(0, bufferIndex));
            }

            var hash = xxHash3.GetCurrentHashAsUInt64();
            xxHash3.Reset();

            foreach (var entryIndex in list)
            {
                var entry = chunks[entryIndex];

                if (entry.RollingHash != checksum || entry.Hash != hash)
                {
                    continue;
                }

                // The start is our current read position minus chunksize since we already read the chunk
                var start = inputStream.Position - chunkSize;
                var entryIndexStart = entryIndex * chunkSize;
                var existingSlice = slices[entryIndex];

                // If there is no slice, or the start matches our entry exactly
                if (existingSlice == null || start == entryIndexStart)
                {
                    slices[entryIndex] = new PatchSlice(PatchSliceLocation.ExistingSlice, start);
                }
            }
        }

        // For all of the slices that are still null, set them to remote since we didn't find one
        for (var i = 0; i < slices.Length; i++)
        {
            slices[i] ??= new PatchSlice(PatchSliceLocation.RemoteSlice, i * chunkSize);
        }

        processingResult.Completed();
        callback?.Invoke(processingResult);
        return slices;
    }
}
