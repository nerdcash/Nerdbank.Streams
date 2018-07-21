﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE.txt file in the project root for full license information.

namespace Nerdbank.Streams
{
    using System;
    using System.Buffers;
    using System.IO;
    using System.IO.Pipelines;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;

    /// <summary>
    /// Stream extension methods.
    /// </summary>
    public static class StreamExtensions
    {
        /// <summary>
        /// Creates a <see cref="Stream"/> that can read no more than a given number of bytes from an underlying stream.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="length">The number of bytes to read from the parent stream.</param>
        /// <returns>A stream that ends after <paramref name="length"/> bytes are read.</returns>
        public static Stream ReadSlice(this Stream stream, long length) => new NestedStream(stream, length);

        /// <summary>
        /// Exposes a full-duplex pipe as a <see cref="Stream"/>.
        /// </summary>
        /// <param name="pipe">The pipe to wrap as a stream.</param>
        /// <returns>The wrapping stream.</returns>
        public static Stream AsStream(this IDuplexPipe pipe) => new PipeStream(pipe);

        /// <summary>
        /// Exposes a pipe reader as a <see cref="Stream"/>.
        /// </summary>
        /// <param name="pipeReader">The pipe to read from when <see cref="Stream.ReadAsync(byte[], int, int, CancellationToken)"/> is invoked.</param>
        /// <returns>The wrapping stream.</returns>
        public static Stream AsStream(this PipeReader pipeReader) => new PipeStream(pipeReader);

        /// <summary>
        /// Exposes a pipe writer as a <see cref="Stream"/>.
        /// </summary>
        /// <param name="pipeWriter">The pipe to write to when <see cref="Stream.WriteAsync(byte[], int, int, CancellationToken)"/> is invoked.</param>
        /// <returns>The wrapping stream.</returns>
        public static Stream AsStream(this PipeWriter pipeWriter) => new PipeStream(pipeWriter);

        /// <summary>
        /// Enables efficiently reading a stream using <see cref="PipeReader"/>.
        /// </summary>
        /// <param name="stream">The stream to read from using a pipe.</param>
        /// <param name="readBufferSize">The size of the buffer to ask the stream to fill.</param>
        /// <param name="cancellationToken">A cancellation token that will cancel task that reads from the stream to fill the pipe.</param>
        /// <returns>A <see cref="PipeReader"/>.</returns>
        public static PipeReader UsePipeReader(this Stream stream, int readBufferSize = 2048, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(stream, nameof(stream));
            Requires.Argument(stream.CanRead, nameof(stream), "Stream must be readable.");

            var pipe = new Pipe();
            Task.Run(async delegate
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Memory<byte> memory = pipe.Writer.GetMemory(readBufferSize);
                    try
                    {
                        int bytesRead = await stream.ReadAsync(memory, cancellationToken);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        pipe.Writer.Advance(bytesRead);
                    }
                    catch (Exception ex)
                    {
                        pipe.Writer.Complete(ex);
                        throw;
                    }

                    FlushResult result = await pipe.Writer.FlushAsync();
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }

                // Tell the PipeReader that there's no more data coming
                pipe.Writer.Complete();
            }).Forget();
            return pipe.Reader;
        }

        /// <summary>
        /// Enables writing to a stream using <see cref="PipeWriter"/>.
        /// </summary>
        /// <param name="stream">The stream to write to using a pipe.</param>
        /// <param name="cancellationToken">A cancellation token that aborts writing.</param>
        /// <returns>A <see cref="PipeWriter"/>.</returns>
        public static PipeWriter UsePipeWriter(this Stream stream, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(stream, nameof(stream));
            Requires.Argument(stream.CanWrite, nameof(stream), "Stream must be writable.");

            var pipe = new Pipe();
            Task.Run(async delegate
            {
                try
                {
                    while (true)
                    {
                        ReadResult readResult = await pipe.Reader.ReadAsync(cancellationToken);
                        if (readResult.Buffer.Length > 0)
                        {
                            foreach (ReadOnlyMemory<byte> segment in readResult.Buffer)
                            {
                                await stream.WriteAsync(segment, cancellationToken);
                            }

                            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        pipe.Reader.AdvanceTo(readResult.Buffer.End);

                        if (readResult.IsCompleted)
                        {
                            break;
                        }
                    }

                    pipe.Reader.Complete();
                }
                catch (Exception ex)
                {
                    pipe.Reader.Complete(ex);
                    throw;
                }
            }).Forget();
            return pipe.Writer;
        }

#if !SPAN_BUILTIN
#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix

        /// <summary>
        /// Reads from the stream into a memory buffer.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="buffer">The buffer to read directly into.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>The number of bytes actually read.</returns>
        /// <devremarks>
        /// This method shamelessly copied from the .NET Core 2.1 Stream class: https://github.com/dotnet/coreclr/blob/a113b1c803783c9d64f1f0e946ff9a853e3bc140/src/System.Private.CoreLib/shared/System/IO/Stream.cs#L366-L391.
        /// </devremarks>
        internal static ValueTask<int> ReadAsync(this Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(stream, nameof(stream));

            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> array))
            {
                return new ValueTask<int>(stream.ReadAsync(array.Array, array.Offset, array.Count, cancellationToken));
            }
            else
            {
                byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
                return FinishReadAsync(stream.ReadAsync(sharedBuffer, 0, buffer.Length, cancellationToken), sharedBuffer, buffer);

                async ValueTask<int> FinishReadAsync(Task<int> readTask, byte[] localBuffer, Memory<byte> localDestination)
                {
                    try
                    {
                        int result = await readTask.ConfigureAwait(false);
                        new Span<byte>(localBuffer, 0, result).CopyTo(localDestination.Span);
                        return result;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(localBuffer);
                    }
                }
            }
        }

        /// <summary>
        /// Writes to a stream from a memory buffer.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        /// <param name="buffer">The buffer to read from.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A task that indicates when the write operation is complete.</returns>
        /// <devremarks>
        /// This method shamelessly copied from the .NET Core 2.1 Stream class: https://github.com/dotnet/coreclr/blob/a113b1c803783c9d64f1f0e946ff9a853e3bc140/src/System.Private.CoreLib/shared/System/IO/Stream.cs#L672-L696.
        /// </devremarks>
        internal static ValueTask WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(stream, nameof(stream));

            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> array))
            {
                return new ValueTask(stream.WriteAsync(array.Array, array.Offset, array.Count, cancellationToken));
            }
            else
            {
                byte[] sharedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
                buffer.Span.CopyTo(sharedBuffer);
                return new ValueTask(FinishWriteAsync(stream.WriteAsync(sharedBuffer, 0, buffer.Length, cancellationToken), sharedBuffer));
            }

            async Task FinishWriteAsync(Task writeTask, byte[] localBuffer)
            {
                try
                {
                    await writeTask.ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(localBuffer);
                }
            }
        }

#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix
#endif
    }
}
