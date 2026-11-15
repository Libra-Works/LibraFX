using System;

namespace LiberaFx.Unity
{
    // A small self-contained v2 program, not an engine callback or substitute
    // evaluator. Time -> multiply(default gain) -> add(0.2) -> splat4 -> color.
    public static class LiberaFxSampleProgram
    {
        public const string TimeId = "00000000-0000-0000-0000-000000000001";
        public const string GainId = "00000000-0000-0000-0000-000000000002";
        public const string MotionId = GainId;
        public const string ColorId = "00000000-0000-0000-0000-000000000005";

        public static byte[] Create()
        {
            var bytes = new byte[16 + 5 * 96];
            bytes[0] = (byte)'L'; bytes[1] = (byte)'F'; bytes[2] = (byte)'X'; bytes[3] = (byte)'B';
            bytes[4] = 2; bytes[6] = 1; bytes[8] = 5; bytes[12] = 96;
            Node(bytes, 0, 2, 1, 0, uint.MaxValue);
            Node(bytes, 1, 5, 1, 1, 0);
            Number(bytes, 16 + 96 + 64, 0.2);
            Node(bytes, 2, 3, 1, 1, 1);
            Number(bytes, 16 + 2 * 96 + 64, 0.2);
            Node(bytes, 3, 25, 4, 1, 2);
            Node(bytes, 4, 39, 4, 4, 3);
            bytes[16 + 4 * 96 + 20] = 1; // Explicit color output semantic.
            return bytes;
        }
        private static void Node(byte[] bytes, int index, byte opcode, byte width, byte inputWidth, uint source)
        {
            var offset = 16 + index * 96;
            bytes[offset + 15] = (byte)(index + 1);
            bytes[offset + 16] = opcode;
            bytes[offset + 17] = width;
            bytes[offset + 18] = inputWidth;
            // Scalar binary nodes use a disconnected B default; its encoded width is zero.
            Write32(bytes, offset + 24, source);
            Write32(bytes, offset + 28, uint.MaxValue);
        }
        private static void Write32(byte[] bytes, int offset, uint value)
        {
            for (var i = 0; i < 4; ++i) bytes[offset + i] = (byte)(value >> (8 * i));
        }
        private static void Number(byte[] bytes, int offset, double value)
        {
            var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            for (var i = 0; i < 8; ++i) bytes[offset + i] = (byte)(bits >> (8 * i));
        }
    }
}
