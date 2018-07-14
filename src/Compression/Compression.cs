// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Compression;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using ObjCRuntime;

namespace Compression
{
	[iOS (9,0), Mac (10,11)]
	public partial class CompressionStream : Stream
	{
		private const int DefaultBufferSize = 8192;

		private Stream _stream;
		private CompressionMode _mode;
		private bool _leaveOpen;
		private Inflater _inflater;
		private Deflater _deflater;
		private byte[] _buffer;
		private int _activeAsyncOperation; // 1 == true, 0 == false
		private bool _wroteBytes;

		// Implies mode = Compress
		public CompressionStream (Stream stream, CompressionAlgorithm algorithm) : this (stream, algorithm, leaveOpen: false)
		{
		}

		// Implies mode = Compress
		public CompressionStream (Stream stream, CompressionAlgorithm algorithm, bool leaveOpen) : this (stream, CompressionMode.Compress, algorithm, leaveOpen)
		{
		}

		public CompressionStream (Stream stream, CompressionMode mode, CompressionAlgorithm algorithm) : this (stream, mode, algorithm, leaveOpen: false)
		{
		}

		/// <summary>
		/// Internal constructor to check stream validity and call the correct initialization function depending on
		/// the value of the CompressionMode given.
		/// </summary>
		public CompressionStream (Stream stream, CompressionMode mode, CompressionAlgorithm algorithm, bool leaveOpen)
		{
			if (stream == null)
				throw new ArgumentNullException (nameof (stream));

			switch (mode) {
			case CompressionMode.Decompress:
				InitializeInflater (stream, algorithm, leaveOpen);
				break;
			case CompressionMode.Compress:
				InitializeDeflater (stream, algorithm, leaveOpen);
				break;
			default:
				throw new ArgumentException ("Enum value was out of legal range.", nameof (mode));
			}
		}

		internal void InitializeInflater (Stream stream, CompressionAlgorithm algorithm, bool leaveOpen)
		{
			if (!stream.CanRead)
				throw new ArgumentException ("Stream does not support reading.", nameof (stream));

			_inflater = new Inflater (algorithm);
			_stream = stream;
			_mode = CompressionMode.Decompress;
			_leaveOpen = leaveOpen;
		}

		internal void InitializeDeflater (Stream stream, CompressionAlgorithm algorithm, bool leaveOpen)
		{
			if (!stream.CanWrite)
				throw new ArgumentException ("Stream does not support writing.", nameof (stream));

			_deflater = new Deflater (algorithm);
			_stream = stream;
			_mode = CompressionMode.Compress;
			_leaveOpen = leaveOpen;
			InitializeBuffer ();
		}

		private void InitializeBuffer ()
		{
			_buffer = ArrayPool<byte>.Shared.Rent (DefaultBufferSize);
		}

		private void EnsureBufferInitialized ()
		{
			if (_buffer == null) {
				InitializeBuffer ();
			}
		}

		public Stream BaseStream => _stream;

		public override bool CanRead
		{
			get {
				if (_stream == null) {
					return false;
				}

				return (_mode == CompressionMode.Decompress && _stream.CanRead);
			}
		}

		public override bool CanWrite {
			get {
				if (_stream == null) {
					return false;
				}

				return (_mode == CompressionMode.Compress && _stream.CanWrite);
			}
		}

		public override bool CanSeek => false;

		public override long Length
		{
			get { throw new NotSupportedException ("This operation is not supported."); }
		}

		public override long Position
		{
			get { throw new NotSupportedException ("This operation is not supported."); }
			set { throw new NotSupportedException ("This operation is not supported."); }
		}

		public override void Flush ()
		{
			EnsureNotDisposed ();
			if (_mode == CompressionMode.Compress)
				FlushBuffers ();
		}

		public override Task FlushAsync (CancellationToken cancellationToken)
		{
			EnsureNoActiveAsyncOperation ();
			EnsureNotDisposed ();

			if (cancellationToken.IsCancellationRequested)
				return Task.FromCanceled (cancellationToken);

			return _mode != CompressionMode.Compress || !_wroteBytes ? Task.CompletedTask : FlushAsyncCore (cancellationToken);
		}

		private async Task FlushAsyncCore (CancellationToken cancellationToken)
		{
			AsyncOperationStarting ();
			try {
				// Compress any bytes left:
				await WriteDeflaterOutputAsync (cancellationToken).ConfigureAwait (false);

				// Pull out any bytes left inside deflater:
				bool flushSuccessful;
				do {
					int compressedBytes;
					flushSuccessful = _deflater.Flush (_buffer, out compressedBytes);
					if (flushSuccessful) {
						await _stream.WriteAsync(_buffer, 0, compressedBytes, cancellationToken).ConfigureAwait (false);
					}
				} while (flushSuccessful);
			} finally {
				AsyncOperationCompleting ();
			}
		}

		public override long Seek (long offset, SeekOrigin origin)
		{
			throw new NotSupportedException ("This operation is not supported.");
		}

		public override void SetLength (long value)
		{
			throw new NotSupportedException ("This operation is not supported.");
		}

		public override int ReadByte ()
		{
			EnsureDecompressionMode ();
			EnsureNotDisposed ();

			// Try to read a single byte from zlib without allocating an array, pinning an array, etc.
			// If zlib doesn't have any data, fall back to the base stream implementation, which will do that.
			byte b;
			return _inflater.Inflate (out b) ? b : base.ReadByte ();
		}

		public override int Read (byte[] array, int offset, int count)
		{
			ValidateParameters (array, offset, count);
			return ReadCore (new Span<byte> (array, offset, count));
		}

		public override int Read (Span<byte> destination)
		{
			if (GetType () != typeof (CompressionStream)) {
				// CompressStream is not sealed, and a derived type may have overridden Read(byte[], int, int) prior
				// to this Read(Span<byte>) overload being introduced.  In that case, this Read(Span<byte>) overload
				// should use the behavior of Read(byte[],int,int) overload.
				return base.Read (destination);
			} else {
				return ReadCore (destination);
			}
		}

		internal int ReadCore (Span<byte> destination)
		{
			EnsureDecompressionMode ();
			EnsureNotDisposed ();
			EnsureBufferInitialized ();

			int totalRead = 0;

			while (true) {
				int bytesRead = _inflater.Inflate (destination.Slice (totalRead));
				totalRead += bytesRead;
				if (totalRead == destination.Length) {
					break;
				}

				if (_inflater.Finished ()) {
					break;
				}

				int bytes = _stream.Read (_buffer, 0, _buffer.Length);
				if (bytes <= 0) {
					break;
				}
				else if (bytes > _buffer.Length) {
					// The stream is either malicious or poorly implemented and returned a number of
					// bytes larger than the buffer supplied to it.
					throw new InvalidDataException ("Found invalid data while decoding.");
				}

				_inflater.SetInput (_buffer, 0, bytes);
			}

			return totalRead;
		}

		private void ValidateParameters (byte[] array, int offset, int count)
		{
			if (array == null)
				throw new ArgumentNullException (nameof (array));

			if (offset < 0)
				throw new ArgumentOutOfRangeException (nameof (offset));

			if (count < 0)
				throw new ArgumentOutOfRangeException (nameof (count));

			if (array.Length - offset < count)
				throw new ArgumentException ("Offset plus count is larger than the length of target array.");
		}

		private void EnsureNotDisposed ()
		{
			if (_stream == null)
				ThrowStreamClosedException ();
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private static void ThrowStreamClosedException ()
		{
			throw new ObjectDisposedException (null, "Can not access a closed Stream.");
		}

		private void EnsureDecompressionMode ()
		{
			if (_mode != CompressionMode.Decompress)
				ThrowCannotReadFromDeflateStreamException ();
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private static void ThrowCannotReadFromDeflateStreamException ()
		{
			throw new InvalidOperationException ("Reading from the compression stream is not supported.");
		}

		private void EnsureCompressionMode ()
		{
			if (_mode != CompressionMode.Compress)
				ThrowCannotWriteToDeflateStreamException ();
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private static void ThrowCannotWriteToDeflateStreamException()
		{
			throw new InvalidOperationException ("Writing to the compression stream is not supported.");
		}

		public override IAsyncResult BeginRead (byte[] buffer, int offset, int count, AsyncCallback asyncCallback, object asyncState) =>
			TaskToApm.Begin(ReadAsync (buffer, offset, count, CancellationToken.None), asyncCallback, asyncState);

		public override int EndRead (IAsyncResult asyncResult) =>
			TaskToApm.End<int> (asyncResult);

		public override Task<int> ReadAsync (byte[] array, int offset, int count, CancellationToken cancellationToken)
		{
			ValidateParameters (array, offset, count);
			return ReadAsyncMemory (new Memory<byte>(array, offset, count), cancellationToken).AsTask ();
		}

		public override ValueTask<int> ReadAsync (Memory<byte> destination, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (GetType () != typeof (CompressionStream)) {
				// Ensure that existing streams derived from DeflateStream and that override ReadAsync(byte[],...)
				// get their existing behaviors when the newer Memory-based overload is used.
				return base.ReadAsync (destination, cancellationToken);
			} else {
				return ReadAsyncMemory (destination, cancellationToken);
			}
		}

		internal ValueTask<int> ReadAsyncMemory (Memory<byte> destination, CancellationToken cancellationToken)
		{
			EnsureDecompressionMode ();
			EnsureNoActiveAsyncOperation ();
			EnsureNotDisposed ();

			if (cancellationToken.IsCancellationRequested) {
				return new ValueTask<int>(Task.FromCanceled<int> (cancellationToken));
			}

			EnsureBufferInitialized ();

			bool cleanup = true;
			AsyncOperationStarting ();
			try {
				// Try to read decompressed data in output buffer
				int bytesRead = _inflater.Inflate (destination.Span);
				if (bytesRead != 0) {
					// If decompression output buffer is not empty, return immediately.
					return new ValueTask<int> (bytesRead);
				}

				if (_inflater.Finished ()) {
					// end of compression stream
					return new ValueTask<int> (0);
				}

				// If there is no data on the output buffer and we are not at
				// the end of the stream, we need to get more data from the base stream
				ValueTask<int> readTask = _stream.ReadAsync (_buffer, cancellationToken);
				cleanup = false;
				return FinishReadAsyncMemory (readTask, destination, cancellationToken);
			} finally {
				// if we haven't started any async work, decrement the counter to end the transaction
				if (cleanup) {
					AsyncOperationCompleting ();
				}
			}
		}

		private async ValueTask<int> FinishReadAsyncMemory (
			ValueTask<int> readTask, Memory<byte> destination, CancellationToken cancellationToken)
		{
			try {
				while (true) {
					int bytesRead = await readTask.ConfigureAwait (false);
					EnsureNotDisposed ();

					if (bytesRead <= 0) {
						// This indicates the base stream has received EOF
						return 0;
					} else if (bytesRead > _buffer.Length) {
						// The stream is either malicious or poorly implemented and returned a number of
						// bytes larger than the buffer supplied to it.
						throw new InvalidDataException ("Found invalid data while decoding.");
					}

					cancellationToken.ThrowIfCancellationRequested ();

					// Feed the data from base stream into decompression engine
					_inflater.SetInput (_buffer, 0, bytesRead);
					bytesRead = _inflater.Inflate (destination.Span);

					if (bytesRead == 0 && !_inflater.Finished ()) {
						// We could have read in head information and didn't get any data.
						// Read from the base stream again.
						readTask = _stream.ReadAsync (_buffer, cancellationToken);
					} else {
						return bytesRead;
					}
				}
			} finally {
				AsyncOperationCompleting ();
			}
		}

		public override void Write (byte[] array, int offset, int count)
		{
			ValidateParameters (array, offset, count);
			WriteCore (new ReadOnlySpan<byte> (array, offset, count));
		}

		public override void Write (ReadOnlySpan<byte> source)
		{
			if (GetType () != typeof (CompressionStream)) {
				// DeflateStream is not sealed, and a derived type may have overridden Write(byte[], int, int) prior
				// to this Write(ReadOnlySpan<byte>) overload being introduced.  In that case, this Write(ReadOnlySpan<byte>) overload
				// should use the behavior of Write(byte[],int,int) overload.
				base.Write (source);
			} else {
				WriteCore (source);
			}
		}

		internal void WriteCore (ReadOnlySpan<byte> source)
		{
			EnsureCompressionMode ();
			EnsureNotDisposed ();

			// Write compressed the bytes we already passed to the deflater:
			WriteDeflaterOutput ();

			unsafe {
				// Pass new bytes through deflater and write them too:
				fixed (byte* bufferPtr = &MemoryMarshal.GetReference (source)) {
					_deflater.SetInput (bufferPtr, source.Length);
					WriteDeflaterOutput ();
					_wroteBytes = true;
				}
			}
		}

		private void WriteDeflaterOutput ()
		{
			while (!_deflater.NeedsInput ()) {
				int compressedBytes = _deflater.GetDeflateOutput (_buffer);
				if (compressedBytes > 0) {
					_stream.Write (_buffer, 0, compressedBytes);
				}
				if (_deflater.Finished ()) {
					break;
				}
			}
		}

		// This is called by Flush:
		private void FlushBuffers ()
		{
			// Make sure to only "flush" when we actually had some input:
			if (_wroteBytes) {
				// Compress any bytes left:
				WriteDeflaterOutput ();

				// Pull out any bytes left inside deflater:
				bool flushSuccessful;
				do {
					int compressedBytes;
					flushSuccessful = _deflater.Flush (_buffer, out compressedBytes);
					if (flushSuccessful) {
						_stream.Write (_buffer, 0, compressedBytes);
					}
				} while (flushSuccessful);
			}
		}

		// This is called by Dispose:
		private void PurgeBuffers (bool disposing)
		{
			if (!disposing)
				return;

			if (_stream == null)
				return;

			if (_mode != CompressionMode.Compress)
				return;

			// Some deflaters (e.g. ZLib) write more than zero bytes for zero byte inputs.
			// This round-trips and we should be ok with this, but our legacy managed deflater
			// always wrote zero output for zero input and upstack code (e.g. ZipArchiveEntry)
			// took dependencies on it. Thus, make sure to only "flush" when we actually had
			// some input:
			if (_wroteBytes) {
				// Compress any bytes left
				WriteDeflaterOutput ();

				// Pull out any bytes left inside deflater:
				bool finished;
				do {
					int compressedBytes;
					finished = _deflater.Finish (_buffer, out compressedBytes);

					if (compressedBytes > 0)
						_stream.Write(_buffer, 0, compressedBytes);
				} while (!finished);
			} else {
				// In case of zero length buffer, we still need to clean up the native created stream before
				// the object get disposed because eventually ZLibNative.ReleaseHandle will get called during
				// the dispose operation and although it frees the stream but it return error code because the
				// stream state was still marked as in use. The symptoms of this problem will not be seen except
				// if running any diagnostic tools which check for disposing safe handle objects
				bool finished;
				do {
					int compressedBytes;
					finished = _deflater.Finish (_buffer, out compressedBytes);
				} while (!finished);
			}
		}

		protected override void Dispose (bool disposing)
		{
			try {
				PurgeBuffers (disposing);
			} finally {
				// Close the underlying stream even if PurgeBuffers threw.
				// Stream.Close() may throw here (may or may not be due to the same error).
				// In this case, we still need to clean up internal resources, hence the inner finally blocks.
				try {
					if (disposing && !_leaveOpen)
						_stream?.Dispose ();
				} finally {
					_stream = null;

					try {
						_deflater?.Dispose ();
						_inflater?.Dispose ();
					} finally {
						_deflater = null;
						_inflater = null;

						byte[] buffer = _buffer;
						if (buffer != null) {
							_buffer = null;
							if (!AsyncOperationIsActive) {
								ArrayPool<byte>.Shared.Return (buffer);
							}
						}

						base.Dispose (disposing);
					}
				}
			}
		}

		public override IAsyncResult BeginWrite (byte[] array, int offset, int count, AsyncCallback asyncCallback, object asyncState) =>
			TaskToApm.Begin(WriteAsync (array, offset, count, CancellationToken.None), asyncCallback, asyncState);

		public override void EndWrite (IAsyncResult asyncResult) => TaskToApm.End (asyncResult);

		public override Task WriteAsync (byte[] array, int offset, int count, CancellationToken cancellationToken)
		{
			ValidateParameters (array, offset, count);
			return WriteAsyncMemory (new ReadOnlyMemory<byte> (array, offset, count), cancellationToken);
		}

		public override ValueTask WriteAsync (ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
		{
			if (GetType () != typeof (CompressionStream)) {
				// Ensure that existing streams derived from DeflateStream and that override WriteAsync(byte[],...)
				// get their existing behaviors when the newer Memory-based overload is used.
				return base.WriteAsync (source, cancellationToken);
			} else {
				return new ValueTask(WriteAsyncMemory (source, cancellationToken));
			}
		}

		internal Task WriteAsyncMemory (ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
		{
			EnsureCompressionMode ();
			EnsureNoActiveAsyncOperation ();
			EnsureNotDisposed ();

			return cancellationToken.IsCancellationRequested ?
				Task.FromCanceled<int>(cancellationToken) :
				WriteAsyncMemoryCore (source, cancellationToken);
		}

		private async Task WriteAsyncMemoryCore (ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
		{
			AsyncOperationStarting ();
			try {
				await WriteDeflaterOutputAsync (cancellationToken).ConfigureAwait (false);

				// Pass new bytes through deflater
				_deflater.SetInput (source);

				await WriteDeflaterOutputAsync (cancellationToken).ConfigureAwait (false);

				_wroteBytes = true;
			} finally {
				AsyncOperationCompleting ();
			}
		}

		/// <summary>
		/// Writes the bytes that have already been deflated
		/// </summary>
		private async Task WriteDeflaterOutputAsync (CancellationToken cancellationToken)
		{
			while (!_deflater.NeedsInput ()) {
				int compressedBytes = _deflater.GetDeflateOutput (_buffer);
				if (compressedBytes > 0) {
					await _stream.WriteAsync (_buffer, 0, compressedBytes, cancellationToken).ConfigureAwait (false);
				}

				if (_deflater.Finished ()) {
					break;
				}
			}
		}

		public override Task CopyToAsync (Stream destination, int bufferSize, CancellationToken cancellationToken)
		{
			// Validation as base CopyToAsync would do
			// StreamHelpers.ValidateCopyToArgs(this, destination, bufferSize);

			// Validation as ReadAsync would do
			EnsureDecompressionMode ();
			EnsureNoActiveAsyncOperation ();
			EnsureNotDisposed ();

			// Early check for cancellation
			if (cancellationToken.IsCancellationRequested) {
				return Task.FromCanceled<int> (cancellationToken);
			}

			// Do the copy
			return new CopyToAsyncStream (this, destination, bufferSize, cancellationToken).CopyFromSourceToDestination ();
		}

		private sealed class CopyToAsyncStream : Stream
		{
			private readonly CompressionStream _deflateStream;
			private readonly Stream _destination;
			private readonly CancellationToken _cancellationToken;
			private byte[] _arrayPoolBuffer;
			private int _arrayPoolBufferHighWaterMark;

			public CopyToAsyncStream (CompressionStream deflateStream, Stream destination, int bufferSize, CancellationToken cancellationToken)
			{
				if (deflateStream == null)
					throw new ArgumentNullException (nameof (deflateStream));
				if (destination == null)
					throw new ArgumentNullException (nameof (destination));
				if (bufferSize <= 0)
					throw new ArgumentOutOfRangeException (nameof (bufferSize));

				_deflateStream = deflateStream;
				_destination = destination;
				_cancellationToken = cancellationToken;
				_arrayPoolBuffer = ArrayPool<byte>.Shared.Rent (bufferSize);
			}

			public async Task CopyFromSourceToDestination ()
			{
				_deflateStream.AsyncOperationStarting ();
				try {
					// Flush any existing data in the inflater to the destination stream.
					while (true) {
						int bytesRead = _deflateStream._inflater.Inflate (_arrayPoolBuffer, 0, _arrayPoolBuffer.Length);
						if (bytesRead > 0) {
							if (bytesRead > _arrayPoolBufferHighWaterMark) _arrayPoolBufferHighWaterMark = bytesRead;
							await _destination.WriteAsync (_arrayPoolBuffer, 0, bytesRead, _cancellationToken).ConfigureAwait (false);
						}
						else break;
					}

					// Now, use the source stream's CopyToAsync to push directly to our inflater via this helper stream
					await _deflateStream._stream.CopyToAsync (this, _arrayPoolBuffer.Length, _cancellationToken).ConfigureAwait (false);
				} finally {
					_deflateStream.AsyncOperationCompleting ();

					Array.Clear (_arrayPoolBuffer, 0, _arrayPoolBufferHighWaterMark); // clear only the most we used
					ArrayPool<byte>.Shared.Return (_arrayPoolBuffer, clearArray: false);
					_arrayPoolBuffer = null;
				}
			}

			public override async Task WriteAsync (byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			{
				// Validate inputs
				if (buffer == _arrayPoolBuffer)
					throw new ArgumentException (nameof (buffer));
				_deflateStream.EnsureNotDisposed ();
				if (count <= 0) {
					return;
				} else if (count > buffer.Length - offset) {
					// The source stream is either malicious or poorly implemented and returned a number of
					// bytes larger than the buffer supplied to it.
					throw new InvalidDataException ("Found invalid data while decoding.");
				}

				// Feed the data from base stream into the decompression engine.
				_deflateStream._inflater.SetInput (buffer, offset, count);

				// While there's more decompressed data available, forward it to the destination stream.
				while (true) {
					int bytesRead = _deflateStream._inflater.Inflate (_arrayPoolBuffer, 0, _arrayPoolBuffer.Length);
					if (bytesRead > 0) {
						if (bytesRead > _arrayPoolBufferHighWaterMark) _arrayPoolBufferHighWaterMark = bytesRead;
						await _destination.WriteAsync (_arrayPoolBuffer, 0, bytesRead, cancellationToken).ConfigureAwait (false);
					}
					else break;
				}
			}

			public override void Write (byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, default(CancellationToken)).GetAwaiter().GetResult();
			public override bool CanWrite => true;
			public override void Flush () { }

			public override bool CanRead => false;
			public override bool CanSeek => false;
			public override long Length { get { throw new NotSupportedException (); } }
			public override long Position { get { throw new NotSupportedException (); } set { throw new NotSupportedException (); } }
			public override int Read (byte[] buffer, int offset, int count) { throw new NotSupportedException (); }
			public override long Seek (long offset, SeekOrigin origin) { throw new NotSupportedException (); }
			public override void SetLength (long value) { throw new NotSupportedException (); }
		}

		private bool AsyncOperationIsActive => _activeAsyncOperation != 0;

		private void EnsureNoActiveAsyncOperation ()
		{
			if (AsyncOperationIsActive)
				ThrowInvalidBeginCall ();
		}

		private void AsyncOperationStarting ()
		{
			if (Interlocked.CompareExchange (ref _activeAsyncOperation, 1, 0) != 0) {
				ThrowInvalidBeginCall ();
			}
		}

		private void AsyncOperationCompleting ()
		{
			int oldValue = Interlocked.CompareExchange (ref _activeAsyncOperation, 0, 1);
			if (oldValue != 1)
				throw new InvalidOperationException ($"Expected {nameof (_activeAsyncOperation)} to be 1, got {oldValue}");
		}

		private static void ThrowInvalidBeginCall ()
		{
			throw new InvalidOperationException("Only one asynchronous reader or writer is allowed time at one time.");
		}
	}
}
