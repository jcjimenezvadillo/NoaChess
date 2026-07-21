using System.Buffers.Binary;
using NoaChess.Core;

namespace NoaChess.DataGen;

// The NOADATA1 binary dataset format — the contract with
// tools/training/nnue/dataset.py (keep both in sync).
//
// File header (64 bytes, little-endian):
//   0   8  magic "NOADATA1"
//   8   4  format version (u32) = 1
//   12  4  feature schema id (u32) = 1 (see NnueFeatureIndex)
//   16  4  score scale (u32) = 1 (scores are plain centipawns)
//   20  4  record size (u32) = 40
//   24  8  record count (u64) — patched on close
//   32  32 manifest SHA-256 (hash of the manifest.json written alongside)
//
// Record (40 bytes, little-endian):
//   0   8  occupancy bitboard (u64)
//   8   16 piece codes, one nibble per occupied square in ascending square
//           order; code = pieceType(0..5) + 6*color (white 0..5, black 6..11)
//   24  1  side to move (0 white, 1 black)
//   25  1  castling rights (CastlingRights bits)
//   26  1  en passant square (255 = none)
//   27  1  halfmove clock
//   28  2  ply (u16)
//   30  2  score in cp from the side to move (i16)
//   32  1  game result from the side to move (+1/0/-1) (i8)
//   33  1  padding
//   34  2  best move (u16, Move encoding; 0 = not stored)
//   36  4  reserved
public static class DatasetFormat
{
    public const string Magic = "NOADATA1";
    public const uint FormatVersion = 1;
    public const uint FeatureSchemaId = 1;
    public const uint ScoreScale = 1;
    public const int HeaderSize = 64;
    public const int RecordSize = 40;

    public static void WriteHeader(Stream stream, ulong recordCount, ReadOnlySpan<byte> manifestSha)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        System.Text.Encoding.ASCII.GetBytes(Magic).CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], FeatureSchemaId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], ScoreScale);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], RecordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], recordCount);
        manifestSha.CopyTo(header[32..]);
        stream.Write(header);
    }

    // Packs the position + labels into a 40-byte record.
    public static void WriteRecord(Span<byte> dest, Board board, int ply, int scoreCp, int resultStm)
    {
        dest.Clear();

        ulong occupancy = board.AllOccupancy;
        BinaryPrimitives.WriteUInt64LittleEndian(dest, occupancy);

        // One nibble per occupied square, ascending square order.
        int nibble = 0;
        ulong occ = occupancy;
        while (occ != 0)
        {
            int sq = Bitboard.PopLsb(ref occ);
            int code = (int)board.PieceTypeAt(sq) + (board.ColorAt(sq) == Color.White ? 0 : 6);
            int byteIndex = 8 + nibble / 2;
            if ((nibble & 1) == 0)
                dest[byteIndex] = (byte)code;
            else
                dest[byteIndex] |= (byte)(code << 4);
            nibble++;
        }

        dest[24] = (byte)board.SideToMove;
        dest[25] = (byte)board.CastlingRights;
        dest[26] = board.EnPassantSquare == Squares.None ? (byte)255 : (byte)board.EnPassantSquare;
        dest[27] = (byte)Math.Min(255, board.HalfmoveClock);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[28..], (ushort)ply);
        BinaryPrimitives.WriteInt16LittleEndian(dest[30..], (short)Math.Clamp(scoreCp, -32000, 32000));
        dest[32] = (byte)(sbyte)resultStm;
    }
}
