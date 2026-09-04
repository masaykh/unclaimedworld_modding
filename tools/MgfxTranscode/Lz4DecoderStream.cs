// Vendored verbatim from the decompiled shipped MonoGame 3.6 build:
//   decomp/MonoGame.Framework/Microsoft/Xna/Framework/Utilities/Lz4DecoderStream.cs
// MonoGame is Ms-PL. This is the exact decoder that reads the game's LZ4 XNBs, so using
// it rather than a fresh LZ4 implementation guarantees bit-identical decompression.
// Do not edit; re-vendor from decomp/ if it ever needs updating.
using System;
using System.IO;

namespace UW.Tools.MgfxTranscode;

internal class Lz4DecoderStream : Stream
{
	private enum DecodePhase
	{
		ReadToken,
		ReadExLiteralLength,
		CopyLiteral,
		ReadOffset,
		ReadExMatchLength,
		CopyMatch
	}

	private const int DecBufLen = 65536;

	private const int DecBufMask = 65535;

	private const int InBufLen = 128;

	private long inputLength;

	private Stream input;

	private byte[] decodeBuffer = new byte[65664];

	private int decodeBufferPos;

	private int inBufPos;

	private int inBufEnd;

	private DecodePhase phase;

	private int litLen;

	private int matLen;

	private int matDst;

	public override bool CanRead => true;

	public override bool CanSeek => false;

	public override bool CanWrite => false;

	public override long Length
	{
		get
		{
			throw new NotSupportedException();
		}
	}

	public override long Position
	{
		get
		{
			throw new NotSupportedException();
		}
		set
		{
			throw new NotSupportedException();
		}
	}

	public Lz4DecoderStream()
	{
	}

	public Lz4DecoderStream(Stream input, long inputLength = long.MaxValue)
	{
		Reset(input, inputLength);
	}

	public void Reset(Stream input, long inputLength = long.MaxValue)
	{
		this.inputLength = inputLength;
		this.input = input;
		phase = DecodePhase.ReadToken;
		decodeBufferPos = 0;
		litLen = 0;
		matLen = 0;
		matDst = 0;
		inBufPos = 65536;
		inBufEnd = 65536;
	}

	protected override void Dispose(bool disposing)
	{
		input = null;
		base.Dispose(disposing);
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		if (buffer == null)
		{
			throw new ArgumentNullException("buffer");
		}
		if (offset < 0 || count < 0 || buffer.Length - count < offset)
		{
			throw new ArgumentOutOfRangeException();
		}
		if (input == null)
		{
			throw new InvalidOperationException();
		}
		int num = count;
		byte[] array = decodeBuffer;
		int num7;
		switch (phase)
		{
		default:
		{
			int num2;
			if (inBufPos < inBufEnd)
			{
				num2 = array[inBufPos++];
			}
			else
			{
				num2 = ReadByteCore();
				if (num2 == -1)
				{
					break;
				}
			}
			litLen = num2 >> 4;
			matLen = (num2 & 0xF) + 4;
			int num3 = litLen;
			if (num3 != 0)
			{
				if (num3 == 15)
				{
					phase = DecodePhase.ReadExLiteralLength;
					goto case DecodePhase.ReadExLiteralLength;
				}
				phase = DecodePhase.CopyLiteral;
				goto case DecodePhase.CopyLiteral;
			}
			phase = DecodePhase.ReadOffset;
			goto case DecodePhase.ReadOffset;
		}
		case DecodePhase.ReadExLiteralLength:
			while (true)
			{
				int num15;
				if (inBufPos < inBufEnd)
				{
					num15 = array[inBufPos++];
				}
				else
				{
					num15 = ReadByteCore();
					if (num15 == -1)
					{
						break;
					}
				}
				litLen += num15;
				if (num15 == 255)
				{
					continue;
				}
				goto IL_012d;
			}
			break;
		case DecodePhase.CopyLiteral:
			do
			{
				int num4 = ((litLen < num) ? litLen : num);
				if (num4 == 0)
				{
					break;
				}
				if (inBufPos + num4 <= inBufEnd)
				{
					int num5 = offset;
					int num6 = num4;
					while (num6-- != 0)
					{
						buffer[num5++] = array[inBufPos++];
					}
					num7 = num4;
				}
				else
				{
					num7 = ReadCore(buffer, offset, num4);
					if (num7 == 0)
					{
						goto end_IL_0045;
					}
				}
				offset += num7;
				num -= num7;
				litLen -= num7;
			}
			while (litLen != 0);
			if (num == 0)
			{
				break;
			}
			phase = DecodePhase.ReadOffset;
			goto case DecodePhase.ReadOffset;
		case DecodePhase.ReadOffset:
			if (inBufPos + 1 < inBufEnd)
			{
				matDst = (array[inBufPos + 1] << 8) | array[inBufPos];
				inBufPos += 2;
			}
			else
			{
				matDst = ReadOffsetCore();
				if (matDst == -1)
				{
					break;
				}
			}
			if (matLen == 19)
			{
				phase = DecodePhase.ReadExMatchLength;
				goto case DecodePhase.ReadExMatchLength;
			}
			phase = DecodePhase.CopyMatch;
			goto case DecodePhase.CopyMatch;
		case DecodePhase.ReadExMatchLength:
			while (true)
			{
				int num14;
				if (inBufPos < inBufEnd)
				{
					num14 = array[inBufPos++];
				}
				else
				{
					num14 = ReadByteCore();
					if (num14 == -1)
					{
						break;
					}
				}
				matLen += num14;
				if (num14 == 255)
				{
					continue;
				}
				goto IL_0290;
			}
			break;
		case DecodePhase.CopyMatch:
			{
				int num8 = ((matLen < num) ? matLen : num);
				if (num8 != 0)
				{
					num7 = count - num;
					int num9 = matDst - num7;
					if (num9 > 0)
					{
						int num10 = decodeBufferPos - num9;
						if (num10 < 0)
						{
							num10 += 65536;
						}
						int num11 = ((num9 < num8) ? num9 : num8);
						int num12 = num11;
						while (num12-- != 0)
						{
							buffer[offset++] = array[num10++ & 0xFFFF];
						}
					}
					else
					{
						num9 = 0;
					}
					int num13 = offset - matDst;
					for (int i = num9; i < num8; i++)
					{
						buffer[offset++] = buffer[num13++];
					}
					num -= num8;
					matLen -= num8;
				}
				if (num == 0)
				{
					break;
				}
				phase = DecodePhase.ReadToken;
				goto default;
			}
			IL_0290:
			phase = DecodePhase.CopyMatch;
			goto case DecodePhase.CopyMatch;
			IL_012d:
			phase = DecodePhase.CopyLiteral;
			goto case DecodePhase.CopyLiteral;
			end_IL_0045:
			break;
		}
		num7 = count - num;
		int num16 = ((num7 < 65536) ? num7 : 65536);
		int srcOffset = offset - num16;
		if (num16 == 65536)
		{
			Buffer.BlockCopy(buffer, srcOffset, array, 0, 65536);
			decodeBufferPos = 0;
		}
		else
		{
			int num17 = decodeBufferPos;
			while (num16-- != 0)
			{
				array[num17++ & 0xFFFF] = buffer[srcOffset++];
			}
			decodeBufferPos = num17 & 0xFFFF;
		}
		return num7;
	}

	private int ReadByteCore()
	{
		byte[] array = decodeBuffer;
		if (inBufPos == inBufEnd)
		{
			int num = input.Read(array, 65536, (int)((128 < inputLength) ? 128 : inputLength));
			if (num == 0)
			{
				return -1;
			}
			inputLength -= num;
			inBufPos = 65536;
			inBufEnd = 65536 + num;
		}
		return array[inBufPos++];
	}

	private int ReadOffsetCore()
	{
		byte[] array = decodeBuffer;
		if (inBufPos == inBufEnd)
		{
			int num = input.Read(array, 65536, (int)((128 < inputLength) ? 128 : inputLength));
			if (num == 0)
			{
				return -1;
			}
			inputLength -= num;
			inBufPos = 65536;
			inBufEnd = 65536 + num;
		}
		if (inBufEnd - inBufPos == 1)
		{
			array[65536] = array[inBufPos];
			int num2 = input.Read(array, 65537, (int)((127 < inputLength) ? 127 : inputLength));
			if (num2 == 0)
			{
				inBufPos = 65536;
				inBufEnd = 65537;
				return -1;
			}
			inputLength -= num2;
			inBufPos = 65536;
			inBufEnd = 65536 + num2 + 1;
		}
		int result = (array[inBufPos + 1] << 8) | array[inBufPos];
		inBufPos += 2;
		return result;
	}

	private int ReadCore(byte[] buffer, int offset, int count)
	{
		int num = count;
		byte[] array = decodeBuffer;
		int num2 = inBufEnd - inBufPos;
		int num3 = ((num < num2) ? num : num2);
		if (num3 != 0)
		{
			int num4 = inBufPos;
			int num5 = num3;
			while (num5-- != 0)
			{
				buffer[offset++] = array[num4++];
			}
			inBufPos = num4;
			num -= num3;
		}
		if (num != 0)
		{
			int num6;
			if (num >= 128)
			{
				num6 = input.Read(buffer, offset, (int)((num < inputLength) ? num : inputLength));
				num -= num6;
			}
			else
			{
				num6 = input.Read(array, 65536, (int)((128 < inputLength) ? 128 : inputLength));
				inBufPos = 65536;
				inBufEnd = 65536 + num6;
				num3 = ((num < num6) ? num : num6);
				int num7 = inBufPos;
				int num8 = num3;
				while (num8-- != 0)
				{
					buffer[offset++] = array[num7++];
				}
				inBufPos = num7;
				num -= num3;
			}
			inputLength -= num6;
		}
		return count - num;
	}

	public override void Flush()
	{
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		throw new NotSupportedException();
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException();
	}
}
